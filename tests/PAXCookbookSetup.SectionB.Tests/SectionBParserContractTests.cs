using PAXCookbookSetup;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

namespace PAXCookbookSetup.SectionB.Tests;

internal static class Program
{
    private const string RunnerAssemblyName = "PAXCookbookSetup.SectionB.Tests";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue = BuildOpCodeMap();

    public static int Main(string[] args)
    {
        try
        {
            return args.Length switch
            {
                1 when args[0] == "b2" => RunB2(),
                1 when args[0] == "b3-calibration" => RunCalibration(),
                1 when args[0] == "b3-late-load" => RunLateLoad(),
                1 when args[0] == "empty-argv" => RunEmptyArgv(),
                2 when args[0] == "b3-main" => RunB3Main(args[1]),
                _ => Fail("MODE_INVALID", "Expected b2, b3-calibration, b3-late-load, empty-argv, or b3-main <evidence-path>.")
            };
        }
        catch (RunnerAssertionException ex)
        {
            return Fail(ex.Label, ex.Message);
        }
        catch (Exception ex)
        {
            return Fail("UNHANDLED", ex.GetType().Name);
        }
    }

    private static int RunB2()
    {
        var payloadRoot = Path.Combine(Path.GetTempPath(), "PAX Cookbook Section B", "payload files");
        var rows = new List<object>();
        var assertions = 0;

        var b21 = ArgParser.Parse(["install", "--silent", "--payload-root", payloadRoot]);
        AssertCase(ExactOutcome(b21, "install", payloadRoot, quiet: true, missingVerb: false, []) && !Directory.Exists(payloadRoot), "B2.1");
        assertions++;
        rows.Add(new { label = "B2.1", parsed = b21 });

        var b22 = ArgParser.Parse(["install", "--quiet", "--payload-root", payloadRoot]);
        AssertCase(ExactOutcome(b22, "install", payloadRoot, quiet: true, missingVerb: false, []) && !Directory.Exists(payloadRoot), "B2.2");
        assertions++;
        rows.Add(new { label = "B2.2", parsed = b22 });

        var b23 = ArgParser.Parse(["uninstall", "--silent"]);
        AssertCase(ExactOutcome(b23, "uninstall", null, quiet: true, missingVerb: false, []), "B2.3");
        assertions++;
        rows.Add(new { label = "B2.3", parsed = b23 });

        var b24 = ArgParser.Parse(["/S"]);
        AssertCase(ExactOutcome(b24, "/S", null, quiet: false, missingVerb: false, ["unknown verb: /S"]), "B2.4");
        assertions++;
        rows.Add(new { label = "B2.4", parsed = b24 });

        var optionFirst = ArgParser.Parse(["--silent"]);
        var explicitHelp = ArgParser.Parse(["help", "--silent"]);
        var empty = ArgParser.Parse([]);
        AssertCase(
            ExactOutcome(optionFirst, "help", null, quiet: true, missingVerb: true, []) &&
            optionFirst.Verb != "install" &&
            ExactOutcome(explicitHelp, "help", null, quiet: true, missingVerb: false, []) &&
            ExactOutcome(empty, "help", null, quiet: false, missingVerb: false, []),
            "B2.5");
        assertions++;
        rows.Add(new { label = "B2.5", optionFirst, explicitHelp, empty });

        var b26 = ArgParser.Parse(["install", "--nonsense"]);
        AssertCase(ExactOutcome(b26, "install", null, quiet: false, missingVerb: false, ["unknown argument: --nonsense"]), "B2.6");
        assertions++;
        rows.Add(new { label = "B2.6", parsed = b26 });

        AssertCase(assertions == 6, "B2_COUNT");
        WriteStdout(new { mode = "b2", verdict = "PASS", assertionCount = assertions, rows });
        return 0;
    }

    private static int RunEmptyArgv()
    {
        var parsed = ArgParser.Parse([]);
        AssertCase(ExactOutcome(parsed, "help", null, quiet: false, missingVerb: false, []), "EMPTY_ARGV");
        WriteStdout(new { mode = "empty-argv", verdict = "PASS", parsed });
        return 0;
    }

    private static int RunCalibration()
    {
        var calibration = ExecuteCalibration();
        AssertCase(calibration.Verdict == "PASS", "B3_CALIBRATION");
        WriteStdout(calibration);
        return 0;
    }

    private static int RunLateLoad()
    {
        WarmB3Runtime();
        _ = AssemblyLoadContext.Default;
        var initial = CaptureLoadedModules();
        AssertCase(EnumerationComplete(initial), "B3_LATE_LOAD_INITIAL_ENUMERATION_COMPLETE");
        var scanned = initial.Modules.Where(module => !module.ExcludedFramework && module.Path.Length > 0)
            .Select(module => module.Identity).ToHashSet(StringComparer.Ordinal);

        var negativePath = CalibrationAssemblyPath("Negative", "SectionB.NegativeFixture.dll");
        _ = AssemblyLoadContext.Default.LoadFromAssemblyPath(negativePath);
        var exit = CaptureLoadedModules();
        AssertCase(EnumerationComplete(exit), "B3_LATE_LOAD_EXIT_ENUMERATION_COMPLETE");
        var comparison = CompareExitUniverse(initial, exit, scanned);

        AssertCase(comparison.Verdict == "REFUSE", "B3_LATE_LOAD_VERDICT");
        AssertCase(comparison.UnexaminedExitOnlyModules.Any(module => module.Path == negativePath), "B3_LATE_LOAD_MODULE");
        WriteStdout(new
        {
            mode = "b3-late-load",
            verdict = "PASS",
            expectedPredicateVerdict = "REFUSE",
            initial,
            exit,
            comparison
        });
        return 0;
    }

    private static int RunB3Main(string evidencePath)
    {
        var finalPath = Path.GetFullPath(evidencePath);
        var parent = Path.GetDirectoryName(finalPath);
        AssertCase(parent is not null && Directory.Exists(parent), "B3_EVIDENCE_PARENT");

        WarmB3Runtime();
        var calibration = ExecuteCalibration();
        AssertCase(calibration.Verdict == "PASS", "B3_MAIN_CALIBRATION");

        var initial = CaptureLoadedModules();
        var initialScans = ScanIncludedModules(initial);
        var scannedIdentities = initialScans.Select(scan => scan.ModuleIdentity).ToHashSet(StringComparer.Ordinal);
        var runnerModule = initial.Modules.SingleOrDefault(module => module.AssemblyName == RunnerAssemblyName);
        AssertCase(runnerModule is not null && runnerModule.Path.Length > 0 && runnerModule.Sha256.Length == 64, "B3_RUNNER_IDENTITY");

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            AppDomain.CurrentDomain.ProcessExit -= handler;
            var exit = CaptureLoadedModules();
            var exitOnlyScans = ScanIncludedModules(exit, scannedIdentities);
            foreach (var scan in exitOnlyScans)
            {
                scannedIdentities.Add(scan.ModuleIdentity);
            }

            var comparison = CompareExitUniverse(initial, exit, scannedIdentities);
            var allScans = initialScans.Concat(exitOnlyScans).ToArray();
            var includedHits = allScans.Sum(scan => scan.ProhibitedHits.Count);
            var decodeComplete = allScans.All(scan => scan.DecodeComplete);
            var blankFields = CountBlankClaimFields(initial, exit, allScans, calibration);
            var enumerationComplete = EnumerationComplete(initial) && EnumerationComplete(exit);
            var verdict = calibration.Verdict == "PASS" &&
                          enumerationComplete &&
                          decodeComplete &&
                          includedHits == 0 &&
                          blankFields == 0 &&
                          comparison.UnexaminedExitOnlyModules.Count == 0
                ? "PASS"
                : "REFUSE";

            var evidence = new
            {
                schemaVersion = 1,
                mode = "b3-main",
                verdict,
                finalizedBy = "ProcessExit",
                finalizedUtc = DateTimeOffset.UtcNow,
                scanner = new
                {
                    implementation = "System.Reflection.Metadata/System.Reflection.PortableExecutable",
                    justification = "already shipped in .NET 8, no network/package restore, direct PE metadata and IL byte decoding including MemberRef/MethodDef/MethodSpec and P/Invoke ImplMap metadata, smaller fit under the no-network constraint"
                },
                runnerIdentity = runnerModule,
                initial,
                exit,
                comparison,
                calibration,
                scans = allScans,
                predicates = new
                {
                    positiveDetected = calibration.PositiveDetected,
                    negativeDetected = calibration.NegativeDetected,
                    blankClaimFields = blankFields,
                    enumerationComplete,
                    initialModuleEnumerationFailures = initial.EnumerationFailures.Count,
                    exitModuleEnumerationFailures = exit.EnumerationFailures.Count,
                    decodeComplete,
                    includedUniverseProhibitedHits = includedHits,
                    unexaminedExitOnlyModules = comparison.UnexaminedExitOnlyModules.Count
                }
            };

            WriteJsonAtomically(finalPath, evidence);
            Environment.ExitCode = verdict == "PASS" ? 0 : 2;
        };

        AppDomain.CurrentDomain.ProcessExit += handler;
        WriteStdout(new
        {
            mode = "b3-main",
            state = "PROCESS_EXIT_FINALIZATION_ARMED",
            evidencePath = finalPath,
            initialModuleCount = initial.Modules.Count,
            initialIncludedCount = initialScans.Length
        });
        return 0;
    }

    private static CalibrationEvidence ExecuteCalibration()
    {
        var positivePath = CalibrationAssemblyPath("Positive", "SectionB.PositiveFixture.dll");
        var negativePath = CalibrationAssemblyPath("Negative", "SectionB.NegativeFixture.dll");
        var positive = ScanAssembly(positivePath, "calibration-positive");
        var negative = ScanAssembly(negativePath, "calibration-negative");
        var positiveKinds = positive.ProhibitedHits.Select(hit => hit.Kind).ToHashSet(StringComparer.Ordinal);

        var processStartDirect = IsProhibitedTarget(new TargetIdentity("System.Diagnostics.Process", "Start", false, "")) == "Process.Start";
        var processStartInfoDirect = IsProhibitedTarget(new TargetIdentity("System.Diagnostics.ProcessStartInfo", ".ctor", false, "")) == "ProcessStartInfo.ctor";
        var shellExecuteDirect = IsProhibitedTarget(new TargetIdentity("Calibration.Direct", "ShellExecute", true, "ShellExecuteW")) == "PInvoke.ShellExecute";
        var shellExecuteExDirect = IsProhibitedTarget(new TargetIdentity("Calibration.Direct", "ShellExecuteEx", true, "ShellExecuteExW")) == "PInvoke.ShellExecuteEx";
        var incompleteControl = DecodeIl([0xFE], _ => null);

        var positiveDetected = positive.DecodeComplete &&
                               positiveKinds.Contains("Process.Start") &&
                               positiveKinds.Contains("ProcessStartInfo.ctor") &&
                               positiveKinds.Contains("PInvoke.ShellExecute") &&
                               positiveKinds.Contains("PInvoke.ShellExecuteEx");
        var negativeDetected = negative.ProhibitedHits.Count > 0;
        var directControlsPass = processStartDirect && processStartInfoDirect && shellExecuteDirect && shellExecuteExDirect;
        var incompleteDecodeRefused = !incompleteControl.Complete && incompleteControl.Reason == "TRUNCATED_EXTENDED_OPCODE";
        var enumerationSuccess = CaptureLoadedModules();
        var enumerationFailure = CaptureLoadedModules(static _ => throw new InvalidOperationException());
        var enumerationControl = new ModuleEnumerationControl(
            "EnumerationComplete(ModuleUniverse)",
            EnumerationComplete(enumerationSuccess),
            enumerationSuccess.EnumerationFailures.Count,
            enumerationSuccess.EnumerationFailures,
            !EnumerationComplete(enumerationFailure),
            enumerationFailure.EnumerationFailures.Count,
            enumerationFailure.EnumerationFailures);
        var enumerationControlPassed = enumerationControl.SuccessAccepted &&
                                       enumerationControl.SuccessFailureCount == 0 &&
                                       enumerationControl.InjectedFailureRefused &&
                                       enumerationControl.InjectedFailureCount > 0;
        var blanks = new[] { positive.Path, positive.Sha256, negative.Path, negative.Sha256, incompleteControl.Reason }
            .Count(string.IsNullOrWhiteSpace);
        blanks += enumerationSuccess.EnumerationFailures.Count(failure =>
            string.IsNullOrWhiteSpace(failure.AssemblyIdentity) || string.IsNullOrWhiteSpace(failure.Reason));
        blanks += enumerationFailure.EnumerationFailures.Count(failure =>
            string.IsNullOrWhiteSpace(failure.AssemblyIdentity) || string.IsNullOrWhiteSpace(failure.Reason));
        var verdict = positiveDetected && !negativeDetected && directControlsPass && incompleteDecodeRefused &&
                      enumerationControlPassed && blanks == 0
            ? "PASS"
            : "REFUSE";

        return new CalibrationEvidence(
            "b3-calibration",
            verdict,
            "positive and negative real assemblies scanned by the same PE metadata and IL predicate in this run",
            positive,
            negative,
            positiveDetected,
            negativeDetected,
            new DirectControls(processStartDirect, processStartInfoDirect, shellExecuteDirect, shellExecuteExDirect),
            new IncompleteDecodeControl(incompleteControl.Complete, incompleteControl.Reason, incompleteDecodeRefused),
            enumerationControl,
            blanks);
    }

    private static void WarmB3Runtime()
    {
        var runnerPath = typeof(Program).Module.FullyQualifiedName;
        _ = ScanAssembly(runnerPath, "warmup");
        _ = CaptureLoadedModules();
        _ = JsonSerializer.SerializeToUtf8Bytes(new { warm = true }, JsonOptions);
        _ = SHA256.HashData([1, 2, 3]);
        _ = AssemblyLoadContext.Default;
    }

    private static ModuleScan[] ScanIncludedModules(ModuleUniverse universe, HashSet<string>? alreadyScanned = null)
    {
        var scans = new List<ModuleScan>();
        foreach (var module in universe.Modules.Where(module => !module.ExcludedFramework))
        {
            if (alreadyScanned?.Contains(module.Identity) == true)
            {
                continue;
            }

            if (module.Path.Length == 0)
            {
                scans.Add(new ModuleScan(module.Identity, module.Path, module.Sha256, false, 0, 0, [], ["BLANK_MODULE_PATH"]));
                continue;
            }

            var scan = ScanAssembly(module.Path, module.Identity);
            scans.Add(new ModuleScan(module.Identity, scan.Path, scan.Sha256, scan.DecodeComplete, scan.MethodCount,
                scan.InstructionCount, scan.ProhibitedHits, scan.IncompleteReasons));
        }

        return scans.ToArray();
    }

    private static AssemblyScan ScanAssembly(string path, string label)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return new AssemblyScan(label, fullPath, "", false, 0, 0, [], ["FILE_MISSING"]);
        }

        var hits = new List<ProhibitedHit>();
        var incomplete = new List<string>();
        var methodCount = 0;
        var instructionCount = 0;

        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata)
            {
                return new AssemblyScan(label, fullPath, HashFile(fullPath), false, 0, 0, [], ["NO_METADATA"]);
            }

            var reader = peReader.GetMetadataReader();
            foreach (var methodHandle in reader.MethodDefinitions)
            {
                methodCount++;
                var method = reader.GetMethodDefinition(methodHandle);
                var methodName = reader.GetString(method.Name);
                var declaringType = GetTypeName(reader, method.GetDeclaringType());

                if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                {
                    var import = method.GetImport();
                    var importName = import.Name.IsNil ? methodName : reader.GetString(import.Name);
                    var moduleName = import.Module.IsNil ? "" : reader.GetString(reader.GetModuleReference(import.Module).Name);
                    var target = new TargetIdentity(declaringType, methodName, true, importName);
                    var kind = IsProhibitedTarget(target);
                    if (kind is not null)
                    {
                        hits.Add(new ProhibitedHit(kind, declaringType, methodName, importName, moduleName, "ImplMap"));
                    }
                }

                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                byte[] il;
                try
                {
                    var methodBodyBytes = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                    if (methodBodyBytes is null)
                    {
                        incomplete.Add($"METHOD_BODY:{declaringType}.{methodName}:NULL_IL");
                        continue;
                    }

                    il = methodBodyBytes;
                }
                catch (Exception ex)
                {
                    incomplete.Add($"METHOD_BODY:{declaringType}.{methodName}:{ex.GetType().Name}");
                    continue;
                }

                var decoded = DecodeIl(il, token => ResolveMethodTarget(reader, token));
                instructionCount += decoded.InstructionCount;
                if (!decoded.Complete)
                {
                    incomplete.Add($"IL:{declaringType}.{methodName}:{decoded.Reason}");
                    continue;
                }

                foreach (var target in decoded.Targets)
                {
                    var kind = IsProhibitedTarget(target);
                    if (kind is not null)
                    {
                        hits.Add(new ProhibitedHit(kind, target.DeclaringType, target.MethodName, target.ImportName, "", "IL"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            incomplete.Add($"PE:{ex.GetType().Name}");
        }

        return new AssemblyScan(label, fullPath, HashFile(fullPath), incomplete.Count == 0, methodCount,
            instructionCount, hits, incomplete);
    }

    private static DecodeResult DecodeIl(byte[] il, Func<int, TargetIdentity?> resolveMethod)
    {
        var targets = new List<TargetIdentity>();
        var offset = 0;
        var instructionCount = 0;
        while (offset < il.Length)
        {
            var first = il[offset++];
            ushort key;
            if (first == 0xFE)
            {
                if (offset >= il.Length)
                {
                    return new DecodeResult(false, "TRUNCATED_EXTENDED_OPCODE", instructionCount, targets);
                }

                key = (ushort)(0xFE00 | il[offset++]);
            }
            else
            {
                key = first;
            }

            if (!OpCodesByValue.TryGetValue(key, out var opCode))
            {
                return new DecodeResult(false, $"UNKNOWN_OPCODE_{key:X4}", instructionCount, targets);
            }

            instructionCount++;
            var operandOffset = offset;
            if (!TryOperandSize(opCode.OperandType, il, operandOffset, out var operandSize, out var reason))
            {
                return new DecodeResult(false, reason, instructionCount, targets);
            }

            if (operandOffset > il.Length - operandSize)
            {
                return new DecodeResult(false, $"TRUNCATED_OPERAND_{opCode.Name}", instructionCount, targets);
            }

            if (IsMethodTokenInstruction(opCode))
            {
                if (operandSize != 4)
                {
                    return new DecodeResult(false, $"METHOD_TOKEN_SIZE_{opCode.Name}", instructionCount, targets);
                }

                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandOffset, 4));
                TargetIdentity? target;
                try
                {
                    target = resolveMethod(token);
                }
                catch (Exception ex)
                {
                    return new DecodeResult(false, $"TOKEN_RESOLUTION_{ex.GetType().Name}", instructionCount, targets);
                }

                if (target is null)
                {
                    return new DecodeResult(false, $"UNRESOLVED_METHOD_TOKEN_{token:X8}", instructionCount, targets);
                }

                targets.Add(target);
            }

            offset += operandSize;
        }

        return new DecodeResult(true, "COMPLETE", instructionCount, targets);
    }

    private static bool TryOperandSize(OperandType operandType, byte[] il, int offset, out int size, out string reason)
    {
        reason = "COMPLETE";
        size = operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod or
                OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or
                OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => -1,
            _ => -2
        };

        if (size >= 0)
        {
            return true;
        }

        if (size == -2)
        {
            reason = $"UNKNOWN_OPERAND_TYPE_{operandType}";
            return false;
        }

        if (offset > il.Length - 4)
        {
            reason = "TRUNCATED_SWITCH_COUNT";
            return false;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));
        if (count < 0 || count > (il.Length - offset - 4) / 4)
        {
            reason = "INVALID_SWITCH_COUNT";
            return false;
        }

        size = 4 + (count * 4);
        return true;
    }

    private static bool IsMethodTokenInstruction(OpCode opCode) =>
        opCode == OpCodes.Call || opCode == OpCodes.Callvirt || opCode == OpCodes.Newobj ||
        opCode == OpCodes.Ldftn || opCode == OpCodes.Ldvirtftn;

    private static TargetIdentity? ResolveMethodTarget(MetadataReader reader, int token)
    {
        var handle = MetadataTokens.Handle(token);
        return handle.Kind switch
        {
            HandleKind.MemberReference => ResolveMemberReference(reader, (MemberReferenceHandle)handle),
            HandleKind.MethodDefinition => ResolveMethodDefinition(reader, (MethodDefinitionHandle)handle),
            HandleKind.MethodSpecification => ResolveMethodSpecification(reader, (MethodSpecificationHandle)handle),
            _ => null
        };
    }

    private static TargetIdentity ResolveMemberReference(MetadataReader reader, MemberReferenceHandle handle)
    {
        var member = reader.GetMemberReference(handle);
        return new TargetIdentity(GetParentTypeName(reader, member.Parent), reader.GetString(member.Name), false, "");
    }

    private static TargetIdentity ResolveMethodDefinition(MetadataReader reader, MethodDefinitionHandle handle)
    {
        var method = reader.GetMethodDefinition(handle);
        var methodName = reader.GetString(method.Name);
        var importName = "";
        var pinvoke = (method.Attributes & MethodAttributes.PinvokeImpl) != 0;
        if (pinvoke)
        {
            var import = method.GetImport();
            importName = import.Name.IsNil ? methodName : reader.GetString(import.Name);
        }

        return new TargetIdentity(GetTypeName(reader, method.GetDeclaringType()), methodName, pinvoke, importName);
    }

    private static TargetIdentity? ResolveMethodSpecification(MetadataReader reader, MethodSpecificationHandle handle)
    {
        var specification = reader.GetMethodSpecification(handle);
        return specification.Method.Kind switch
        {
            HandleKind.MemberReference => ResolveMemberReference(reader, (MemberReferenceHandle)specification.Method),
            HandleKind.MethodDefinition => ResolveMethodDefinition(reader, (MethodDefinitionHandle)specification.Method),
            _ => null
        };
    }

    private static string GetParentTypeName(MetadataReader reader, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeDefinition => GetTypeName(reader, (TypeDefinitionHandle)parent),
        HandleKind.TypeReference => GetTypeName(reader, (TypeReferenceHandle)parent),
        HandleKind.TypeSpecification => "<type-specification>",
        HandleKind.ModuleReference => $"<module:{reader.GetString(reader.GetModuleReference((ModuleReferenceHandle)parent).Name)}>",
        HandleKind.MethodDefinition => ResolveMethodDefinition(reader, (MethodDefinitionHandle)parent).DeclaringType,
        _ => $"<{parent.Kind}>"
    };

    private static string GetTypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    private static string GetTypeName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    private static string? IsProhibitedTarget(TargetIdentity target)
    {
        if (target.DeclaringType == "System.Diagnostics.Process" && target.MethodName == "Start")
        {
            return "Process.Start";
        }

        if (target.DeclaringType == "System.Diagnostics.ProcessStartInfo" && target.MethodName == ".ctor")
        {
            return "ProcessStartInfo.ctor";
        }

        var importName = target.ImportName.Length == 0 ? target.MethodName : target.ImportName;
        if (target.IsPInvoke && importName.StartsWith("ShellExecuteEx", StringComparison.OrdinalIgnoreCase))
        {
            return "PInvoke.ShellExecuteEx";
        }

        if (target.IsPInvoke && importName.StartsWith("ShellExecute", StringComparison.OrdinalIgnoreCase))
        {
            return "PInvoke.ShellExecute";
        }

        return null;
    }

    private static ModuleUniverse CaptureLoadedModules(Func<Assembly, Module[]>? enumerateModules = null)
    {
        enumerateModules ??= static assembly => assembly.GetModules(false);
        var runtimeDirectory = EnsureTrailingSeparator(Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory()));
        var modules = new List<LoadedModuleIdentity>();
        var enumerationFailures = new List<ModuleEnumerationFailure>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(assembly => assembly.FullName, StringComparer.Ordinal))
        {
            Module[] loadedModules;
            try
            {
                loadedModules = enumerateModules(assembly);
            }
            catch (Exception exception)
            {
                enumerationFailures.Add(new ModuleEnumerationFailure(
                    BoundedAssemblyIdentity(assembly),
                    ModuleEnumerationReason(exception)));
                continue;
            }

            foreach (var module in loadedModules.OrderBy(module => module.Name, StringComparer.Ordinal))
            {
                var path = ModulePath(module);
                var excluded = path.Length > 0 && EnsureTrailingSeparator(Path.GetDirectoryName(path) ?? "") == runtimeDirectory;
                var sha = path.Length > 0 && File.Exists(path) ? HashFile(path) : "";
                var identity = $"{assembly.FullName}|{module.Name}|{module.ModuleVersionId:D}|{path}";
                modules.Add(new LoadedModuleIdentity(identity, assembly.GetName().Name ?? "", assembly.FullName ?? "",
                    module.Name, module.ModuleVersionId.ToString("D"), path, sha, excluded,
                    excluded ? "exact module path is directly beneath the installed .NET runtime directory" : "included"));
            }
        }

        return new ModuleUniverse(DateTimeOffset.UtcNow, runtimeDirectory.TrimEnd(Path.DirectorySeparatorChar), modules,
            enumerationFailures);
    }

    private static bool EnumerationComplete(ModuleUniverse universe) => universe.EnumerationFailures.Count == 0;

    private static string BoundedAssemblyIdentity(Assembly assembly)
    {
        var identity = assembly.FullName;
        if (string.IsNullOrWhiteSpace(identity))
        {
            identity = "UNKNOWN_ASSEMBLY";
        }

        return identity.Length <= 512 ? identity : identity[..512];
    }

    private static string ModuleEnumerationReason(Exception exception)
    {
        var typeName = exception.GetType().FullName ?? exception.GetType().Name;
        var typeToken = new string(typeName.Select(character => char.IsLetterOrDigit(character)
            ? char.ToUpperInvariant(character)
            : '_').ToArray());
        if (typeToken.Length == 0)
        {
            typeToken = "UNKNOWN_EXCEPTION_TYPE";
        }

        const string prefix = "GET_MODULES_";
        var maximumTypeLength = 128 - prefix.Length;
        return prefix + (typeToken.Length <= maximumTypeLength ? typeToken : typeToken[..maximumTypeLength]);
    }

    private static ExitUniverseComparison CompareExitUniverse(ModuleUniverse initial, ModuleUniverse exit, HashSet<string> examined)
    {
        var initialIds = initial.Modules.Select(module => module.Identity).ToHashSet(StringComparer.Ordinal);
        var exitOnly = exit.Modules.Where(module => !initialIds.Contains(module.Identity)).ToArray();
        var unexamined = exitOnly.Where(module => !module.ExcludedFramework && !examined.Contains(module.Identity)).ToArray();
        return new ExitUniverseComparison(unexamined.Length == 0 ? "PASS" : "REFUSE", exitOnly, unexamined,
            unexamined.Length == 0 ? "ALL_EXIT_ONLY_INCLUDED_MODULES_EXAMINED" : "UNEXAMINED_EXIT_ONLY_INCLUDED_MODULE");
    }

    private static int CountBlankClaimFields(ModuleUniverse initial, ModuleUniverse exit, ModuleScan[] scans, CalibrationEvidence calibration)
    {
        var blanks = calibration.BlankClaimFields;
        blanks += initial.Modules.Where(module => !module.ExcludedFramework).Count(module =>
            string.IsNullOrWhiteSpace(module.Identity) || string.IsNullOrWhiteSpace(module.Path) || string.IsNullOrWhiteSpace(module.Sha256));
        blanks += exit.Modules.Where(module => !module.ExcludedFramework).Count(module =>
            string.IsNullOrWhiteSpace(module.Identity) || string.IsNullOrWhiteSpace(module.Path) || string.IsNullOrWhiteSpace(module.Sha256));
        blanks += initial.EnumerationFailures.Count(failure =>
            string.IsNullOrWhiteSpace(failure.AssemblyIdentity) || string.IsNullOrWhiteSpace(failure.Reason));
        blanks += exit.EnumerationFailures.Count(failure =>
            string.IsNullOrWhiteSpace(failure.AssemblyIdentity) || string.IsNullOrWhiteSpace(failure.Reason));
        blanks += scans.Count(scan => string.IsNullOrWhiteSpace(scan.ModuleIdentity) || string.IsNullOrWhiteSpace(scan.Path) || string.IsNullOrWhiteSpace(scan.Sha256));
        return blanks;
    }

    private static string ModulePath(Module module)
    {
        try
        {
            var path = module.FullyQualifiedName;
            return path.Length == 0 || path[0] == '<' ? "" : Path.GetFullPath(path);
        }
        catch
        {
            return "";
        }
    }

    private static string CalibrationAssemblyPath(string fixture, string fileName)
    {
        var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        return Path.GetFullPath(Path.Combine(projectRoot, "Calibration", fixture, "bin", "Release", "net8.0", fileName));
    }

    private static bool ExactOutcome(ParsedArgs parsed, string verb, string? payloadRoot, bool quiet, bool missingVerb, string[] errors) =>
        parsed.Verb == verb &&
        parsed.InstallRootOverride is null &&
        parsed.PayloadRoot == payloadRoot &&
        !parsed.Force &&
        !parsed.ReinstallSameVersion &&
        !parsed.AllowDowngrade &&
        !parsed.HandoffFromInstalled &&
        parsed.HandoffFolder is null &&
        !parsed.DryRun &&
        !parsed.RemoveUserData &&
        !parsed.ConfirmRemoveUserData &&
        parsed.Errors.SequenceEqual(errors, StringComparer.Ordinal) &&
        parsed.Quiet == quiet &&
        !parsed.GuiUninstall &&
        parsed.TestIsolationDescriptor is null &&
        parsed.MissingVerb == missingVerb &&
        !parsed.IsSameVersionRepair;

    private static IReadOnlyDictionary<ushort, OpCode> BuildOpCodeMap() =>
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => unchecked((ushort)opCode.Value));

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static void WriteJsonAtomically(string path, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, true);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = JsonDocument.Parse(stream);
        _ = document.RootElement.ValueKind;
    }

    private static void WriteStdout(object value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static void AssertCase(bool condition, string label)
    {
        if (!condition)
        {
            throw new RunnerAssertionException(label, "assertion_failed");
        }
    }

    private static int Fail(string label, string detail)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { verdict = "FAIL", label, detail }));
        return 1;
    }

    private sealed class RunnerAssertionException(string label, string message) : Exception(message)
    {
        public string Label { get; } = label;
    }

    private sealed record TargetIdentity(string DeclaringType, string MethodName, bool IsPInvoke, string ImportName);
    private sealed record ProhibitedHit(string Kind, string DeclaringType, string MethodName, string ImportName, string NativeModule, string Source);
    private sealed record DecodeResult(bool Complete, string Reason, int InstructionCount, IReadOnlyList<TargetIdentity> Targets);
    private sealed record AssemblyScan(string Label, string Path, string Sha256, bool DecodeComplete, int MethodCount,
        int InstructionCount, IReadOnlyList<ProhibitedHit> ProhibitedHits, IReadOnlyList<string> IncompleteReasons);
    private sealed record ModuleScan(string ModuleIdentity, string Path, string Sha256, bool DecodeComplete, int MethodCount,
        int InstructionCount, IReadOnlyList<ProhibitedHit> ProhibitedHits, IReadOnlyList<string> IncompleteReasons);
    private sealed record DirectControls(bool ProcessStart, bool ProcessStartInfoConstructor, bool ShellExecute, bool ShellExecuteEx);
    private sealed record IncompleteDecodeControl(bool DecodeComplete, string Reason, bool Refused);
    private sealed record ModuleEnumerationControl(string Predicate, bool SuccessAccepted, int SuccessFailureCount,
        IReadOnlyList<ModuleEnumerationFailure> SuccessFailures, bool InjectedFailureRefused, int InjectedFailureCount,
        IReadOnlyList<ModuleEnumerationFailure> InjectedFailures);
    private sealed record CalibrationEvidence(string Mode, string Verdict, string Universe, AssemblyScan Positive, AssemblyScan Negative,
        bool PositiveDetected, bool NegativeDetected, DirectControls DirectControls, IncompleteDecodeControl IncompleteDecodeControl,
        ModuleEnumerationControl ModuleEnumerationControl, int BlankClaimFields);
    private sealed record LoadedModuleIdentity(string Identity, string AssemblyName, string AssemblyFullName, string ModuleName,
        string ModuleVersionId, string Path, string Sha256, bool ExcludedFramework, string Disposition);
    private sealed record ModuleEnumerationFailure(string AssemblyIdentity, string Reason);
    private sealed record ModuleUniverse(DateTimeOffset CapturedUtc, string FrameworkDirectory,
        IReadOnlyList<LoadedModuleIdentity> Modules, IReadOnlyList<ModuleEnumerationFailure> EnumerationFailures);
    private sealed record ExitUniverseComparison(string Verdict, IReadOnlyList<LoadedModuleIdentity> ExitOnlyModules,
        IReadOnlyList<LoadedModuleIdentity> UnexaminedExitOnlyModules, string Reason);
}