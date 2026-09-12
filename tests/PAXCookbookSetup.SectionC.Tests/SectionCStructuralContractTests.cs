using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace PAXCookbookSetup.SectionC.Tests;

internal static class Program
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodeMap = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => unchecked((ushort)code.Value));

    public static int Main(string[] args)
    {
        try
        {
            var arguments = ParseArguments(args);
            var positive = ScanManagedAssembly(arguments["positive"]);
            var negative = ScanManagedAssembly(arguments["negative"]);
            var targetManaged = ScanManagedAssembly(arguments["echo-dll"]);
            var targetExecutable = ScanExecutable(arguments["echo-exe"]);

            var requiredCategories = new[]
            {
                "certificate",
                "file-write",
                "gui-message-loop",
                "network",
                "process-creation",
                "registry-write",
                "service-control"
            };
            var positiveCategories = positive.Findings.Select(finding => finding.Category).Distinct(StringComparer.Ordinal).Order().ToArray();
            var positivePassed = positive.DecodeComplete && requiredCategories.All(category => positiveCategories.Contains(category, StringComparer.Ordinal));
            var negativePassed = negative.DecodeComplete && negative.Findings.Count == 0;
            var targetPassed = targetManaged.DecodeComplete
                && targetManaged.Findings.Count == 0
                && targetExecutable.DecodeComplete
                && targetExecutable.IsConsoleSubsystem
                && string.Equals(targetExecutable.FileName, "PaxArgvEcho.exe", StringComparison.Ordinal)
                && string.Equals(targetManaged.AssemblyName, "PaxArgvEcho", StringComparison.Ordinal);

            var result = new
            {
                schemaVersion = "1.0",
                predicate = new
                {
                    universe = new[]
                    {
                        "managed assembly references",
                        "managed metadata P/Invoke declarations",
                        "all decodable managed IL method/member operands",
                        "native PE subsystem of the launched executable"
                    },
                    prohibitedCategories = requiredCategories,
                    incompleteDecodeDisposition = "REFUSE"
                },
                positiveControl = new { expected = "PROHIBITED_FINDINGS", passed = positivePassed, scan = positive },
                negativeControl = new { expected = "ZERO_FINDINGS", passed = negativePassed, scan = negative },
                target = new { expected = "CONSOLE_AND_ZERO_FINDINGS", passed = targetPassed, executable = targetExecutable, managed = targetManaged },
                overall = positivePassed && negativePassed && targetPassed ? "PASS" : "REFUSE"
            };

            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return positivePassed && negativePassed && targetPassed ? 0 : 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SECTION_C_STRUCTURAL_REFUSE={exception.GetType().Name}");
            return 3;
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Arguments must be name/value pairs.");
            }

            result.Add(args[index][2..], Path.GetFullPath(args[index + 1]));
        }

        foreach (var required in new[] { "positive", "negative", "echo-dll", "echo-exe" })
        {
            if (!result.ContainsKey(required))
            {
                throw new InvalidDataException($"Missing argument: {required}");
            }
        }

        return result;
    }

    private static ExecutableScan ScanExecutable(string path)
    {
        using var stream = File.OpenRead(path);
        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        stream.Position = 0;
        using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var header = reader.PEHeaders.PEHeader ?? throw new BadImageFormatException("Missing PE header.");
        return new ExecutableScan(
            path,
            Path.GetFileName(path),
            stream.Length,
            sha256,
            header.Subsystem.ToString(),
            header.Subsystem == Subsystem.WindowsCui,
            true);
    }

    private static ManagedScan ScanManagedAssembly(string path)
    {
        using var stream = File.OpenRead(path);
        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        stream.Position = 0;
        using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!reader.HasMetadata || reader.PEHeaders.CorHeader is null)
        {
            throw new BadImageFormatException("Managed metadata is required.");
        }

        var metadata = reader.GetMetadataReader();
        var methodOwners = BuildMethodOwners(metadata);
        var findings = new List<Finding>();
        var assemblyReferences = new List<string>();
        var pinvokes = new List<string>();
        var decodedMethodCount = 0;
        var decodedInstructionCount = 0;

        foreach (var referenceHandle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(referenceHandle);
            var name = metadata.GetString(reference.Name);
            assemblyReferences.Add(name);
            if (name is "System.Windows.Forms" or "PresentationFramework" or "System.ServiceProcess.ServiceController")
            {
                findings.Add(new Finding("assembly-reference", "assembly", name));
            }
        }

        foreach (var methodHandle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(methodHandle);
            var methodName = metadata.GetString(method.Name);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
            {
                var import = method.GetImport();
                var module = metadata.GetModuleReference(import.Module);
                var moduleName = metadata.GetString(module.Name);
                var importName = metadata.GetString(import.Name);
                var symbol = $"{moduleName}!{importName}";
                pinvokes.Add(symbol);
                var category = ClassifyPInvoke(importName);
                if (category is not null)
                {
                    findings.Add(new Finding(category, "pinvoke", symbol));
                }
            }

            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            var body = reader.GetMethodBody(method.RelativeVirtualAddress);
            var il = body.GetILBytes() ?? throw new BadImageFormatException($"Missing IL in {methodName}.");
            decodedMethodCount++;
            DecodeMethod(metadata, methodOwners, methodName, il, findings, ref decodedInstructionCount);
        }

        var assemblyName = metadata.IsAssembly
            ? metadata.GetString(metadata.GetAssemblyDefinition().Name)
            : string.Empty;
        return new ManagedScan(
            path,
            Path.GetFileName(path),
            assemblyName,
            stream.Length,
            sha256,
            true,
            decodedMethodCount,
            decodedInstructionCount,
            assemblyReferences.Order(StringComparer.Ordinal).ToArray(),
            pinvokes.Order(StringComparer.Ordinal).ToArray(),
            findings.Distinct().OrderBy(finding => finding.Category, StringComparer.Ordinal).ThenBy(finding => finding.Symbol, StringComparer.Ordinal).ToArray());
    }

    private static Dictionary<MethodDefinitionHandle, string> BuildMethodOwners(MetadataReader metadata)
    {
        var result = new Dictionary<MethodDefinitionHandle, string>();
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            var typeName = QualifiedTypeName(metadata, typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                result.Add(methodHandle, typeName);
            }
        }

        return result;
    }

    private static void DecodeMethod(
        MetadataReader metadata,
        IReadOnlyDictionary<MethodDefinitionHandle, string> methodOwners,
        string methodName,
        byte[] il,
        List<Finding> findings,
        ref int instructionCount)
    {
        var offset = 0;
        while (offset < il.Length)
        {
            var first = il[offset++];
            ushort key = first;
            if (first == 0xFE)
            {
                RequireBytes(il, offset, 1, methodName);
                key = (ushort)(0xFE00 | il[offset++]);
            }

            if (!OpCodeMap.TryGetValue(key, out var code))
            {
                throw new BadImageFormatException($"Unknown IL opcode in {methodName}.");
            }

            instructionCount++;
            var operandStart = offset;
            var operandLength = OperandLength(code.OperandType, il, operandStart, methodName);
            if (code.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineTok)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandStart, 4));
                var symbol = ResolveMember(metadata, methodOwners, MetadataTokens.EntityHandle(token));
                var category = ClassifyManagedMember(symbol.TypeName, symbol.MemberName);
                if (category is not null)
                {
                    findings.Add(new Finding(category, "managed-member", $"{symbol.TypeName}.{symbol.MemberName}"));
                }
            }

            offset += operandLength;
        }
    }

    private static int OperandLength(OperandType operandType, byte[] il, int offset, string methodName)
    {
        var length = operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
                or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => SwitchOperandLength(il, offset, methodName),
            _ => throw new BadImageFormatException($"Unsupported operand type {operandType} in {methodName}.")
        };
        RequireBytes(il, offset, length, methodName);
        return length;
    }

    private static int SwitchOperandLength(byte[] il, int offset, string methodName)
    {
        RequireBytes(il, offset, 4, methodName);
        var count = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));
        if (count < 0 || count > (il.Length - offset - 4) / 4)
        {
            throw new BadImageFormatException($"Invalid switch operand in {methodName}.");
        }

        return checked(4 + (count * 4));
    }

    private static void RequireBytes(byte[] il, int offset, int length, string methodName)
    {
        if (offset < 0 || length < 0 || offset > il.Length - length)
        {
            throw new BadImageFormatException($"Truncated IL in {methodName}.");
        }
    }

    private static MemberSymbol ResolveMember(
        MetadataReader metadata,
        IReadOnlyDictionary<MethodDefinitionHandle, string> methodOwners,
        EntityHandle handle)
    {
        return handle.Kind switch
        {
            HandleKind.MemberReference => ResolveMemberReference(metadata, metadata.GetMemberReference((MemberReferenceHandle)handle)),
            HandleKind.MethodDefinition => ResolveMethodDefinition(metadata, methodOwners, (MethodDefinitionHandle)handle),
            HandleKind.MethodSpecification => ResolveMember(metadata, methodOwners, metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method),
            HandleKind.TypeReference => new MemberSymbol(QualifiedTypeName(metadata, (TypeReferenceHandle)handle), string.Empty),
            HandleKind.TypeDefinition => new MemberSymbol(QualifiedTypeName(metadata, (TypeDefinitionHandle)handle), string.Empty),
            HandleKind.TypeSpecification => new MemberSymbol("<type-specification>", string.Empty),
            _ => throw new BadImageFormatException($"Unsupported metadata handle {handle.Kind}.")
        };
    }

    private static MemberSymbol ResolveMemberReference(MetadataReader metadata, MemberReference reference)
    {
        var typeName = reference.Parent.Kind switch
        {
            HandleKind.TypeReference => QualifiedTypeName(metadata, (TypeReferenceHandle)reference.Parent),
            HandleKind.TypeDefinition => QualifiedTypeName(metadata, (TypeDefinitionHandle)reference.Parent),
            HandleKind.TypeSpecification => "<type-specification>",
            HandleKind.ModuleReference => $"<module:{metadata.GetString(metadata.GetModuleReference((ModuleReferenceHandle)reference.Parent).Name)}>",
            HandleKind.MethodDefinition => "<method-definition-parent>",
            _ => throw new BadImageFormatException($"Unsupported member parent {reference.Parent.Kind}.")
        };
        return new MemberSymbol(typeName, metadata.GetString(reference.Name));
    }

    private static MemberSymbol ResolveMethodDefinition(
        MetadataReader metadata,
        IReadOnlyDictionary<MethodDefinitionHandle, string> methodOwners,
        MethodDefinitionHandle handle)
    {
        if (!methodOwners.TryGetValue(handle, out var owner))
        {
            throw new BadImageFormatException("Method owner is missing.");
        }

        return new MemberSymbol(owner, metadata.GetString(metadata.GetMethodDefinition(handle).Name));
    }

    private static string QualifiedTypeName(MetadataReader metadata, TypeReferenceHandle handle)
    {
        var type = metadata.GetTypeReference(handle);
        var name = metadata.GetString(type.Name);
        var ns = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    private static string QualifiedTypeName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        var name = metadata.GetString(type.Name);
        var ns = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    private static string? ClassifyManagedMember(string typeName, string memberName)
    {
        if (typeName == "System.IO.File" && (memberName.StartsWith("Write", StringComparison.Ordinal)
            || memberName.StartsWith("Append", StringComparison.Ordinal)
            || memberName is "Create" or "CreateText" or "OpenWrite" or "Delete" or "Move" or "Copy" or "Replace" or "SetAttributes"))
        {
            return "file-write";
        }

        if (typeName == "Microsoft.Win32.RegistryKey" && (memberName.StartsWith("Set", StringComparison.Ordinal)
            || memberName.StartsWith("Create", StringComparison.Ordinal)
            || memberName.StartsWith("Delete", StringComparison.Ordinal)))
        {
            return "registry-write";
        }

        if ((typeName == "System.Net.Http.HttpClient" && memberName is "Send" or "SendAsync" or "GetAsync" or "PostAsync" or "PutAsync" or "DeleteAsync")
            || (typeName.StartsWith("System.Net.Sockets.", StringComparison.Ordinal) && memberName.StartsWith("Connect", StringComparison.Ordinal)))
        {
            return "network";
        }

        if (typeName == "System.Diagnostics.Process" && memberName == "Start")
        {
            return "process-creation";
        }

        if (typeName == "System.ServiceProcess.ServiceController" && memberName is "Start" or "Stop" or "Pause" or "Continue" or "ExecuteCommand")
        {
            return "service-control";
        }

        if (typeName == "System.Security.Cryptography.X509Certificates.X509Store" && memberName is "Open" or "Add" or "AddRange" or "Remove" or "RemoveRange")
        {
            return "certificate";
        }

        if ((typeName == "System.Windows.Forms.Application" || typeName == "System.Windows.Application") && memberName == "Run")
        {
            return "gui-message-loop";
        }

        return null;
    }

    private static string? ClassifyPInvoke(string importName)
    {
        if (StartsWithAny(importName, "CreateFile", "WriteFile", "DeleteFile", "MoveFile", "CopyFile", "ReplaceFile")) return "file-write";
        if (StartsWithAny(importName, "RegSetValue", "RegCreateKey", "RegDeleteKey", "RegDeleteValue")) return "registry-write";
        if (StartsWithAny(importName, "WinHttp", "InternetOpen", "WSAConnect", "connect")) return "network";
        if (StartsWithAny(importName, "CreateProcess", "ShellExecute", "WinExec")) return "process-creation";
        if (StartsWithAny(importName, "OpenSCManager", "CreateService", "StartService", "ControlService", "DeleteService")) return "service-control";
        if (StartsWithAny(importName, "CertOpenStore", "CertAdd", "CertDelete", "PFXImport")) return "certificate";
        if (StartsWithAny(importName, "GetMessage", "PeekMessage", "DispatchMessage", "DialogBox", "CreateWindow")) return "gui-message-loop";
        return null;
    }

    private static bool StartsWithAny(string value, params string[] prefixes) =>
        prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private sealed record MemberSymbol(string TypeName, string MemberName);
    private sealed record Finding(string Category, string Kind, string Symbol);
    private sealed record ExecutableScan(string Path, string FileName, long Size, string Sha256, string Subsystem, bool IsConsoleSubsystem, bool DecodeComplete);
    private sealed record ManagedScan(
        string Path,
        string FileName,
        string AssemblyName,
        long Size,
        string Sha256,
        bool DecodeComplete,
        int DecodedMethodCount,
        int DecodedInstructionCount,
        IReadOnlyList<string> AssemblyReferences,
        IReadOnlyList<string> PInvokes,
        IReadOnlyList<Finding> Findings);
}