// PAX Cookbook - EMBEDDED SERVICE PAYLOAD RESOURCE (cycle 61)
//
// SECURITY CLASSIFICATION. Reading and checking an embedded resource from
// inside the assembly that carries it is a corruption check, never a tamper
// check. This helper is UNSIGNED PRERELEASE CODE: it does not resist
// replacement by a local user, UAC may show an unknown publisher, and
// prerelease validation proves functionality, not production tamper resistance.
// That is unacceptable for GA, and GA activation stays BLOCKED until this exact
// helper is Authenticode signed and the expected publisher policy is configured
// and verified. In-helper verification is defence in depth only: by the time it
// runs, the code has already begun executing.
//
// WHAT THIS FILE IS. The FIXED, no-argument payload inspection entry point.
// There is no path parameter, no assembly parameter, no resource-name
// parameter, no delegate, no options object and no settable static field, so a
// caller can never redirect what is inspected. It reads bytes into memory and
// hands them to the bytes-only verifier. It never extracts, never writes and
// never launches anything.
using System;
using System.IO;
using System.Reflection;

namespace PAXCookbook.ServiceAdminHelper.Payload;

/// <summary>
/// The single embedded-payload access point for this helper.
/// </summary>
internal static class ServicePayloadResource
{
    /// <summary>
    /// The fixed logical resource name. Exposed so the focused tests can pin it;
    /// it is a compile-time constant and cannot be changed at runtime.
    /// </summary>
    internal static string ResourceName => ServicePayloadArchiveFormat.ResourceName;

    /// <summary>
    /// Inspect the payload embedded in THIS assembly. Takes no argument by
    /// design: there is no path, no assembly, no resource name and no options
    /// object a caller could substitute.
    /// </summary>
    internal static ServicePayloadVerificationResult InspectEmbeddedPayload()
    {
        byte[]? bytes = TryReadEmbeddedBytes(out ServicePayloadVerificationOutcome failure);
        return bytes is null
            ? ServicePayloadVerificationResult.Refused(failure)
            : ServicePayloadArchiveVerifier.Verify(bytes);
    }

    /// <summary>
    /// The disclosed test seam. INTERNAL, accepts BYTES only - never a path,
    /// never a directory and never a destination - so it cannot widen the
    /// surface. It exists so the focused tests can drive the verifier with
    /// constructed archives without rebuilding the helper for every case.
    /// </summary>
    internal static ServicePayloadVerificationResult InspectBytes(ReadOnlyMemory<byte> archiveBytes) =>
        ServicePayloadArchiveVerifier.Verify(archiveBytes);

    /// <summary>
    /// Read the fixed resource from this assembly into memory, or return null
    /// when it is absent. Kept separate so absence and ambiguity are
    /// distinguishable bounded states rather than a single failure.
    /// </summary>
    internal static byte[]? TryReadEmbeddedBytes(out ServicePayloadVerificationOutcome failure)
    {
        failure = ServicePayloadVerificationOutcome.Unspecified;
        try
        {
            Assembly assembly = typeof(ServicePayloadResource).Assembly;
            string[] names = assembly.GetManifestResourceNames();
            int matches = 0;
            foreach (string candidate in names)
            {
                if (string.Equals(candidate, ServicePayloadArchiveFormat.ResourceName, StringComparison.Ordinal))
                {
                    matches++;
                }
            }

            if (matches == 0)
            {
                failure = ServicePayloadVerificationOutcome.ResourceMissing;
                return null;
            }

            if (matches > 1)
            {
                failure = ServicePayloadVerificationOutcome.ResourceAmbiguous;
                return null;
            }

            using Stream? stream = assembly.GetManifestResourceStream(ServicePayloadArchiveFormat.ResourceName);
            if (stream is null)
            {
                failure = ServicePayloadVerificationOutcome.ResourceMissing;
                return null;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception)
        {
            failure = ServicePayloadVerificationOutcome.Unavailable;
            return null;
        }
    }
}
