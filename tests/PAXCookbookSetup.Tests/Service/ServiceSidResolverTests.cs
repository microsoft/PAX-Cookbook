using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 39 - FIXED SERVICE SID RESOLVER (Setup only)
// ===========================================================================
//
// WHAT THE PRODUCT CODE UNDER TEST IS. ServiceSidResolver is a READ-ONLY,
// FIXED-NAME name-to-SID lookup. It never creates, changes, deletes, starts, or
// stops a service; never reads or writes an ACL; never opens a certificate store
// or a private key; never reads or writes the registry; never reads a credential
// vault; never starts a process; never elevates; never opens a socket; never
// touches PAX and never starts a Bake. It has exactly TWO native call sites, no
// retry, no fallback, no alternate name, and no delegate.
//
// HOW IT IS SPLIT (ruling B1). There is NO injectable production native-call
// seam. Instead:
//   * ServiceSidLookupInterpreter is a PURE interpreter over the observable
//     results of the two documented native calls. Every state-mapping decision
//     lives there, and every one of them is driven directly below with no I/O.
//   * ServiceSidResolver.ResolveFixedServiceSid() is a FIXED SHIM that takes no
//     parameters and always uses ServiceIdentityContract.QualifiedServiceAccountName.
//     Its fixed name and its call count are proven STRUCTURALLY, from source, with
//     firing positive controls.
//
// WHAT IS NOT PROVEN HERE. Nothing in this file resolves
// NT SERVICE\PAXCookbookService, and nothing here proves that account exists on
// any machine. The one live lookup in this file (ruling D1) resolves
// NT SERVICE\TrustedInstaller and proves MARSHALLING ONLY.
public sealed class ServiceSidResolverInterpreterTests
{
    // ---- synthetic SID byte fixtures -----------------------------------------
    //
    // Every byte array below is SYNTHETIC and built in-process. Nothing is read
    // from the machine.

    private static byte[] SidBytes(byte revision, ulong authority, params uint[] subs)
    {
        var buffer = new byte[8 + (subs.Length * 4)];
        buffer[0] = revision;
        buffer[1] = (byte)subs.Length;
        for (int i = 0; i < 6; i++)
        {
            buffer[2 + i] = (byte)((authority >> (8 * (5 - i))) & 0xFF);
        }
        for (int i = 0; i < subs.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8 + (i * 4)), subs[i]);
        }
        return buffer;
    }

    private static byte[] ServiceSid() => SidBytes(1, 5UL, 80U, 1U);

    private static ServiceSidBufferPlan PlanFor(byte[] sid) =>
        ServiceSidLookupInterpreter.InterpretFirstCall(false, 122, sid.Length, 12);

    // =======================================================================
    // FIRST CALL
    // =======================================================================

    [Fact]
    public void The_first_call_is_accepted_when_it_failed_and_reported_bounded_sizes()
    {
        ServiceSidBufferPlan plan = ServiceSidLookupInterpreter.InterpretFirstCall(false, 122, 16, 12);

        Assert.True(plan.Proceed);
        Assert.Equal(16, plan.SidBufferBytes);
        Assert.Equal(12, plan.DomainBufferChars);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]      // ERROR_ACCESS_DENIED
    [InlineData(122)]    // ERROR_INSUFFICIENT_BUFFER
    [InlineData(1332)]   // ERROR_NONE_MAPPED
    [InlineData(1789)]
    [InlineData(int.MaxValue)]
    [InlineData(-2147024809)]
    public void The_first_call_acceptance_never_requires_a_particular_native_error_code(int errorCode)
    {
        // RULING A1. The documented page never guarantees ERROR_INSUFFICIENT_BUFFER,
        // so acceptance depends ONLY on: returned zero, bounded required SID size,
        // bounded required domain size.
        ServiceSidBufferPlan plan = ServiceSidLookupInterpreter.InterpretFirstCall(false, errorCode, 16, 12);

        Assert.True(plan.Proceed);
        Assert.Equal(16, plan.SidBufferBytes);
        Assert.Equal(12, plan.DomainBufferChars);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(16, 12)]
    [InlineData(-1, -1)]
    public void A_first_call_that_unexpectedly_succeeded_is_an_invalid_native_response(int sidBytes, int domainChars)
    {
        // The documented sequence REQUIRES the sizing call to fail. A success here
        // means the API did not behave as documented, so the resolver fails closed
        // rather than trusting an undocumented shape.
        ServiceSidBufferPlan plan = ServiceSidLookupInterpreter.InterpretFirstCall(true, 0, sidBytes, domainChars);

        Assert.False(plan.Proceed);
        Assert.Equal(ServiceSidResolutionState.InvalidNativeResponse, plan.FailureState);
    }

    [Theory]
    [InlineData(1332, ServiceSidResolutionState.NotFound)]
    [InlineData(5, ServiceSidResolutionState.AccessDenied)]
    [InlineData(0, ServiceSidResolutionState.UnknownFailure)]
    [InlineData(87, ServiceSidResolutionState.UnknownFailure)]
    [InlineData(1355, ServiceSidResolutionState.UnknownFailure)]
    [InlineData(1788, ServiceSidResolutionState.UnknownFailure)]
    public void A_first_call_that_reported_no_size_maps_to_a_bounded_failure(int errorCode, ServiceSidResolutionState expected)
    {
        ServiceSidBufferPlan plan = ServiceSidLookupInterpreter.InterpretFirstCall(false, errorCode, 0, 0);

        Assert.False(plan.Proceed);
        Assert.Equal(expected, plan.FailureState);
    }

    [Fact]
    public void A_zero_sid_size_is_a_failure_even_when_a_domain_size_was_reported()
    {
        ServiceSidBufferPlan plan = ServiceSidLookupInterpreter.InterpretFirstCall(false, 1332, 0, 12);

        Assert.False(plan.Proceed);
        Assert.Equal(ServiceSidResolutionState.NotFound, plan.FailureState);
    }

    [Theory]
    [InlineData(257, 12)]
    [InlineData(16, 257)]
    [InlineData(int.MaxValue, 12)]
    [InlineData(16, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void An_over_limit_required_size_is_refused_before_any_allocation(int sidBytes, int domainChars)
    {
        ServiceSidBufferPlan plan = ServiceSidLookupInterpreter.InterpretFirstCall(false, 122, sidBytes, domainChars);

        Assert.False(plan.Proceed);
        Assert.Equal(ServiceSidResolutionState.BufferLimitExceeded, plan.FailureState);
        Assert.Equal(0, plan.SidBufferBytes);
        Assert.Equal(0, plan.DomainBufferChars);
    }

    [Theory]
    [InlineData(-1, 12)]
    [InlineData(16, -1)]
    [InlineData(int.MinValue, 12)]
    [InlineData(16, int.MinValue)]
    public void A_negative_required_size_is_an_invalid_native_response(int sidBytes, int domainChars)
    {
        ServiceSidBufferPlan plan = ServiceSidLookupInterpreter.InterpretFirstCall(false, 122, sidBytes, domainChars);

        Assert.False(plan.Proceed);
        Assert.Equal(ServiceSidResolutionState.InvalidNativeResponse, plan.FailureState);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void A_nonzero_required_sid_size_below_the_minimum_is_an_invalid_native_response(int sidBytes)
    {
        ServiceSidBufferPlan plan = ServiceSidLookupInterpreter.InterpretFirstCall(false, 122, sidBytes, 12);

        Assert.False(plan.Proceed);
        Assert.Equal(ServiceSidResolutionState.InvalidNativeResponse, plan.FailureState);
    }

    // =======================================================================
    // SECOND CALL
    // =======================================================================

    [Fact]
    public void A_documented_two_call_sequence_resolves_the_canonical_service_sid()
    {
        byte[] sid = ServiceSid();
        ServiceSidBufferPlan plan = PlanFor(sid);
        Assert.True(plan.Proceed);

        ServiceSidResolution result = ServiceSidLookupInterpreter.InterpretSecondCall(
            plan, true, 0, sid.Length, 12, sid, 1);

        Assert.Equal(ServiceSidResolutionState.Resolved, result.State);
        Assert.True(result.IsResolved);
        Assert.Equal("S-1-5-80-1", result.Sid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void The_sid_name_use_value_is_recorded_but_never_over_constrained(int sidNameUse)
    {
        // CORRECTION 4. The grounded page documents SID_NAME_USE as an OUT value;
        // it does not state which values a per-service virtual account must
        // produce. Asserting one would be an invention, so the CANONICAL SID SHAPE
        // is the final identity check and SID_NAME_USE never changes the outcome.
        byte[] sid = ServiceSid();
        ServiceSidBufferPlan plan = PlanFor(sid);

        ServiceSidResolution result = ServiceSidLookupInterpreter.InterpretSecondCall(
            plan, true, 0, sid.Length, 12, sid, sidNameUse);

        Assert.Equal(ServiceSidResolutionState.Resolved, result.State);
        Assert.Equal("S-1-5-80-1", result.Sid);
    }

    [Theory]
    [InlineData(1332, ServiceSidResolutionState.NotFound)]
    [InlineData(5, ServiceSidResolutionState.AccessDenied)]
    [InlineData(0, ServiceSidResolutionState.UnknownFailure)]
    [InlineData(122, ServiceSidResolutionState.UnknownFailure)]
    [InlineData(998, ServiceSidResolutionState.UnknownFailure)]
    public void A_failed_second_call_maps_to_a_bounded_failure(int errorCode, ServiceSidResolutionState expected)
    {
        byte[] sid = ServiceSid();
        ServiceSidBufferPlan plan = PlanFor(sid);

        ServiceSidResolution result = ServiceSidLookupInterpreter.InterpretSecondCall(
            plan, false, errorCode, sid.Length, 12, sid, 1);

        Assert.Equal(expected, result.State);
        Assert.False(result.IsResolved);
        Assert.Equal(string.Empty, result.Sid);
    }

    [Theory]
    [InlineData(0, 12)]
    [InlineData(-1, 12)]
    [InlineData(17, 12)]     // the required size GREW between the two calls
    [InlineData(256, 12)]
    [InlineData(16, 13)]     // the domain size GREW between the two calls
    [InlineData(16, -1)]
    [InlineData(16, int.MaxValue)]
    public void A_returned_size_that_does_not_match_the_plan_fails_closed(int returnedSidBytes, int returnedDomainChars)
    {
        byte[] sid = ServiceSid();
        ServiceSidBufferPlan plan = PlanFor(sid);
        Assert.Equal(16, plan.SidBufferBytes);
        Assert.Equal(12, plan.DomainBufferChars);

        ServiceSidResolution result = ServiceSidLookupInterpreter.InterpretSecondCall(
            plan, true, 0, returnedSidBytes, returnedDomainChars, sid, 1);

        Assert.Equal(ServiceSidResolutionState.InvalidNativeResponse, result.State);
        Assert.Equal(string.Empty, result.Sid);
    }

    [Fact]
    public void A_shrunken_returned_domain_size_is_accepted_because_the_domain_is_never_interpreted()
    {
        // CORRECTION 3. The domain buffer exists ONLY because the API demands one.
        // A smaller returned domain length is normal and must not fail the lookup;
        // the value itself is never read, returned, logged, persisted, compared, or
        // emitted.
        byte[] sid = ServiceSid();
        ServiceSidBufferPlan plan = PlanFor(sid);

        ServiceSidResolution result = ServiceSidLookupInterpreter.InterpretSecondCall(
            plan, true, 0, sid.Length, 0, sid, 1);

        Assert.Equal(ServiceSidResolutionState.Resolved, result.State);
    }

    [Fact]
    public void A_missing_or_short_sid_buffer_is_an_invalid_native_response()
    {
        byte[] sid = ServiceSid();
        ServiceSidBufferPlan plan = PlanFor(sid);

        Assert.Equal(
            ServiceSidResolutionState.InvalidNativeResponse,
            ServiceSidLookupInterpreter.InterpretSecondCall(plan, true, 0, sid.Length, 12, null, 1).State);

        Assert.Equal(
            ServiceSidResolutionState.InvalidNativeResponse,
            ServiceSidLookupInterpreter.InterpretSecondCall(plan, true, 0, sid.Length, 12, new byte[3], 1).State);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)2)]
    [InlineData((byte)7)]
    [InlineData((byte)255)]
    public void Malformed_sid_bytes_map_to_invalid_sid_with_the_exception_text_swallowed(byte revision)
    {
        byte[] sid = SidBytes(revision, 5UL, 80U, 1U);
        ServiceSidBufferPlan plan = PlanFor(sid);

        ServiceSidResolution result = ServiceSidLookupInterpreter.InterpretSecondCall(
            plan, true, 0, sid.Length, 12, sid, 1);

        Assert.Equal(ServiceSidResolutionState.InvalidSid, result.State);
        Assert.Equal(string.Empty, result.Sid);

        // Nothing derived from the exception survives anywhere on the result.
        Assert.Equal("InvalidSid", result.ToString());
    }

    [Fact]
    public void A_sid_outside_the_service_family_maps_to_wrong_authority()
    {
        foreach (byte[] sid in new[]
                 {
                     SidBytes(1, 1UL, 0U),               // S-1-1-0
                     SidBytes(1, 5UL, 18U),              // S-1-5-18
                     SidBytes(1, 5UL, 32U, 544U),        // S-1-5-32-544
                     SidBytes(1, 5UL, 21U, 1U, 2U, 3U),  // a user-shaped SID
                     SidBytes(1, 5UL, 800U, 1U),         // 800 is not 80
                 })
        {
            ServiceSidBufferPlan plan = PlanFor(sid);

            ServiceSidResolution result = ServiceSidLookupInterpreter.InterpretSecondCall(
                plan, true, 0, sid.Length, 12, sid, 1);

            Assert.Equal(ServiceSidResolutionState.WrongAuthority, result.State);
            Assert.Equal(string.Empty, result.Sid);
        }
    }

    [Fact]
    public void The_all_services_group_sid_is_refused_with_its_own_state()
    {
        byte[] sid = SidBytes(1, 5UL, 80U, 0U);   // S-1-5-80-0
        ServiceSidBufferPlan plan = PlanFor(sid);

        ServiceSidResolution result = ServiceSidLookupInterpreter.InterpretSecondCall(
            plan, true, 0, sid.Length, 12, sid, 1);

        Assert.Equal(ServiceSidResolutionState.AllServicesGroup, result.State);
        Assert.Equal(string.Empty, result.Sid);
    }

    [Fact]
    public void A_service_family_sid_that_is_not_a_specific_service_maps_to_invalid_sid()
    {
        byte[] sid = SidBytes(1, 5UL, 80U);   // S-1-5-80, no per-service subauthority
        ServiceSidBufferPlan plan = PlanFor(sid);

        ServiceSidResolution result = ServiceSidLookupInterpreter.InterpretSecondCall(
            plan, true, 0, sid.Length, 12, sid, 1);

        Assert.Equal(ServiceSidResolutionState.InvalidSid, result.State);
    }

    [Fact]
    public void A_stopped_plan_is_propagated_and_no_second_call_result_can_override_it()
    {
        foreach (ServiceSidResolutionState state in new[]
                 {
                     ServiceSidResolutionState.NotFound,
                     ServiceSidResolutionState.AccessDenied,
                     ServiceSidResolutionState.BufferLimitExceeded,
                     ServiceSidResolutionState.InvalidNativeResponse,
                     ServiceSidResolutionState.UnknownFailure,
                 })
        {
            ServiceSidBufferPlan plan = ServiceSidBufferPlan.Stop(state);
            byte[] sid = ServiceSid();

            ServiceSidResolution result = ServiceSidLookupInterpreter.InterpretSecondCall(
                plan, true, 0, sid.Length, 12, sid, 1);

            Assert.Equal(state, result.State);
            Assert.False(result.IsResolved);
        }
    }

    [Fact]
    public void A_bounded_per_service_sid_of_any_documented_length_resolves()
    {
        // Deliberately several DIFFERENT subauthority counts: no exact count is
        // documented for a per-service virtual account SID.
        (uint[] Subs, string Expected)[] cases =
        {
            (new uint[] { 80U, 1U }, "S-1-5-80-1"),
            (new uint[] { 80U, 0U, 1U }, "S-1-5-80-0-1"),
            (new uint[] { 80U, 1U, 2U, 3U, 4U, 5U }, "S-1-5-80-1-2-3-4-5"),
            (new uint[] { 80U, 3139157870U, 2983391045U, 3678747466U, 658725712U, 1809340420U },
                "S-1-5-80-3139157870-2983391045-3678747466-658725712-1809340420"),
        };

        foreach ((uint[] subs, string expected) in cases)
        {
            byte[] sid = SidBytes(1, 5UL, subs);
            ServiceSidBufferPlan plan = PlanFor(sid);

            ServiceSidResolution result = ServiceSidLookupInterpreter.InterpretSecondCall(
                plan, true, 0, sid.Length, 12, sid, 1);

            Assert.Equal(ServiceSidResolutionState.Resolved, result.State);
            Assert.Equal(expected, result.Sid);
            Assert.True(ServiceIdentityContract.IsServiceIdentitySid(result.Sid));
        }
    }

    // =======================================================================
    // RESULT SURFACE - only Resolved is success, and nothing else leaks
    // =======================================================================

    [Fact]
    public void The_result_states_carry_the_exact_documented_numeric_values()
    {
        // CORRECTION 1. Explicit numeric values, and only Resolved is success.
        Assert.Equal(0, (int)ServiceSidResolutionState.Unspecified);
        Assert.Equal(1, (int)ServiceSidResolutionState.Resolved);
        Assert.Equal(2, (int)ServiceSidResolutionState.NotFound);
        Assert.Equal(3, (int)ServiceSidResolutionState.AccessDenied);
        Assert.Equal(4, (int)ServiceSidResolutionState.InvalidNativeResponse);
        Assert.Equal(5, (int)ServiceSidResolutionState.InvalidSid);
        Assert.Equal(6, (int)ServiceSidResolutionState.WrongAuthority);
        Assert.Equal(7, (int)ServiceSidResolutionState.AllServicesGroup);
        Assert.Equal(8, (int)ServiceSidResolutionState.BufferLimitExceeded);
        Assert.Equal(9, (int)ServiceSidResolutionState.UnsupportedPlatform);
        Assert.Equal(10, (int)ServiceSidResolutionState.UnknownFailure);

        Assert.Equal(11, Enum.GetValues<ServiceSidResolutionState>().Length);
        Assert.False(Enum.IsDefined(typeof(ServiceSidResolutionState), 11));
    }

    [Fact]
    public void Every_non_resolved_state_is_a_failure_that_carries_no_sid()
    {
        foreach (ServiceSidResolutionState state in Enum.GetValues<ServiceSidResolutionState>())
        {
            if (state == ServiceSidResolutionState.Resolved)
            {
                continue;
            }

            ServiceSidResolution result = ServiceSidResolution.Failure(state);

            Assert.False(result.IsResolved);
            Assert.Equal(string.Empty, result.Sid);
            Assert.Equal(state.ToString(), result.ToString());
        }

        Assert.False(default(ServiceSidResolution).IsResolved);
        Assert.Equal(string.Empty, default(ServiceSidResolution).Sid);
        Assert.Equal("Unspecified", default(ServiceSidResolution).ToString());
    }

    [Fact]
    public void A_successful_result_carries_only_the_canonical_sid_and_nothing_else()
    {
        byte[] sid = ServiceSid();
        ServiceSidBufferPlan plan = PlanFor(sid);
        ServiceSidResolution result = ServiceSidLookupInterpreter.InterpretSecondCall(
            plan, true, 0, sid.Length, 12, sid, 1);

        Assert.True(result.IsResolved);

        // The SID grammar is the WHOLE permitted surface: no machine name, no user
        // name, no domain output, no localized account name, no exception text, no
        // Win32 message, no native error value.
        Assert.Matches("^S(-[0-9]+)+$", result.Sid);
        Assert.DoesNotContain("\\", result.Sid, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", result.Sid, StringComparison.Ordinal);
        Assert.True(ServiceOwnershipLedgerContract.IsCanonicalSidString(result.Sid));

        // Nothing leaks through ToString() either: it is the bounded state token
        // and NOTHING else.
        Assert.Equal("Resolved", result.ToString());
        Assert.DoesNotContain("S-1-5", result.ToString(), StringComparison.Ordinal);

        // The public shape of the result type is exactly three members plus
        // ToString: no error code, no message, no domain, no machine name.
        string[] members = typeof(ServiceSidResolution)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "IsResolved", "Sid", "State" }, members);
    }

    [Fact]
    public void The_buffer_plan_never_exposes_a_native_error_value()
    {
        string[] members = typeof(ServiceSidBufferPlan)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "DomainBufferChars", "FailureState", "Proceed", "SidBufferBytes" },
            members);

        // The native error code is an INPUT to bounded failure mapping and is never
        // stored, so it cannot be surfaced later.
        Assert.Equal("Continue", ServiceSidBufferPlan.Continue(16, 12).ToString());
        Assert.Equal("NotFound", ServiceSidBufferPlan.Stop(ServiceSidResolutionState.NotFound).ToString());
    }
}

// ===========================================================================
// STRUCTURAL CONTAINMENT - the fixed shim, proven from source
// ===========================================================================
public sealed class ServiceSidResolverStructuralTests
{
    internal static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PAXCookbook.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    internal static string ResolverPath() =>
        Path.Combine(RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceSidResolver.cs");

    internal static string ContractPath() =>
        Path.Combine(RepoRoot(), "src", "PAXCookbook.Shared", "Contracts", "ServiceIdentityContract.cs");

    private static string ResolverRaw() => File.ReadAllText(ResolverPath());

    private static string ResolverCode() => SetupCSharpLexicalScanner.ExtractCode(ResolverRaw());

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    internal static string MethodBody(string code, string signatureFragment)
    {
        int start = code.IndexOf(signatureFragment, StringComparison.Ordinal);
        Assert.True(start >= 0, "signature fragment not found: " + signatureFragment);

        int open = code.IndexOf('{', start);
        Assert.True(open >= 0, "no method body found for: " + signatureFragment);

        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{')
            {
                depth++;
            }
            else if (code[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return code.Substring(open, i - open + 1);
                }
            }
        }

        Assert.Fail("unbalanced braces for: " + signatureFragment);
        return string.Empty;
    }

    // ---- exactly two native call sites, both with the fixed name --------------

    [Fact]
    public void The_resolver_declares_exactly_one_native_import_and_calls_it_exactly_twice()
    {
        string code = ResolverCode();

        // POSITIVE CONTROL: the scanner really read the resolver's code.
        Assert.Contains("class ServiceSidResolver", code, StringComparison.Ordinal);

        Assert.Equal(1, Occurrences(code, "[DllImport("));
        Assert.Equal(1, Occurrences(code, "extern bool LookupAccountNameW("));

        // One declaration + exactly two invocation sites.
        Assert.Equal(3, Occurrences(code, "LookupAccountNameW("));

        // No other native import mechanism entered this file.
        Assert.Equal(0, Occurrences(code, "LibraryImport"));
        Assert.Equal(1, Occurrences(code, "extern "));
    }

    [Fact]
    public void Both_call_sites_use_the_fixed_qualified_account_name_and_nothing_else()
    {
        string code = ResolverCode();
        string body = MethodBody(code, "ServiceSidResolution ResolveFixedServiceSid()");

        // The fixed name appears exactly twice inside the shim: once per call site.
        Assert.Equal(2, Occurrences(body, "ServiceIdentityContract.QualifiedServiceAccountName"));
        Assert.Equal(2, Occurrences(body, "LookupAccountNameW("));

        // ...and the whole file contains no other account-name source.
        string raw = ResolverRaw();
        Assert.Equal(0, Occurrences(raw, "\"NT SERVICE"));
        Assert.Equal(0, Occurrences(code, "ServiceIdentityContract.ServiceName"));
        Assert.Equal(0, Occurrences(code, "ServiceIdentityContract.ServiceAccountDomain"));
        Assert.Equal(0, Occurrences(code, "Environment.GetEnvironmentVariable"));
        Assert.Equal(0, Occurrences(code, "Environment.MachineName"));
        Assert.Equal(0, Occurrences(code, "Environment.UserName"));
    }

    [Fact]
    public void The_shim_has_no_retry_fallback_or_alternate_name()
    {
        string body = MethodBody(ResolverCode(), "ServiceSidResolution ResolveFixedServiceSid()");

        foreach (string token in new[] { "while", "for (", "foreach", "goto", "do {", "Retry", "Fallback", "Alternate" })
        {
            Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        }

        // POSITIVE CONTROL: the extracted body is real, non-trivial shim code.
        Assert.Contains("LookupAccountNameW(", body, StringComparison.Ordinal);
        Assert.True(body.Length > 200, "the extracted shim body looks too small to be real: " + body.Length);
    }

    [Fact]
    public void The_resolver_exposes_no_parameterized_or_overloaded_entry_point()
    {
        Type t = typeof(ServiceSidResolver);

        Assert.True(t.IsAbstract && t.IsSealed, "the resolver must be a static class");

        MethodInfo[] declared = t
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .ToArray();

        string[] names = declared.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "LookupAccountNameW", "ResolveFixedServiceSid" }, names);

        MethodInfo entry = declared.Single(m => m.Name == "ResolveFixedServiceSid");
        Assert.Empty(entry.GetParameters());
        Assert.Equal(typeof(ServiceSidResolution), entry.ReturnType);

        // No instance surface and no retained state of any kind.
        Assert.Empty(t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.Empty(t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.Empty(t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    }

    [Fact]
    public void The_interpreter_holds_no_delegate_and_no_mutable_state()
    {
        foreach (Type t in new[] { typeof(ServiceSidLookupInterpreter), typeof(ServiceSidResolver) })
        {
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                Assert.True(f.IsLiteral || f.IsInitOnly, t.Name + "." + f.Name + " is mutable");
                Assert.False(typeof(Delegate).IsAssignableFrom(f.FieldType), t.Name + "." + f.Name + " is a delegate");
            }

            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (ParameterInfo p in m.GetParameters())
                {
                    Assert.False(
                        typeof(Delegate).IsAssignableFrom(p.ParameterType),
                        t.Name + "." + m.Name + " accepts a delegate");
                }
            }
        }
    }

    [Fact]
    public void The_transient_domain_buffer_is_cleared_and_never_retained()
    {
        string body = MethodBody(ResolverCode(), "ServiceSidResolution ResolveFixedServiceSid()");

        // CORRECTION 3: allocated because the API demands it, cleared immediately,
        // never returned, logged, persisted, compared, or emitted.
        Assert.Contains("finally", body, StringComparison.Ordinal);
        Assert.Contains("Array.Clear(", body, StringComparison.Ordinal);

        // The domain buffer is never handed to anything that could publish it.
        foreach (string token in new[] { "return domain", "new string(domain", "Encoding", "Console", "File.", "Trace.", "Debug." })
        {
            Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_exception_text_can_escape_the_resolver()
    {
        string code = File.ReadAllText(ResolverPath());
        string stripped = SetupCSharpLexicalScanner.ExtractCode(code);

        // Not a single catch clause binds an exception variable, so there is no
        // value from which a message could ever be read.
        Assert.Equal(0, Regex.Matches(stripped, @"catch\s*\(\s*[\w\.]+\s+\w").Count);

        // POSITIVE CONTROL: the same regex DOES fire on a binding catch clause.
        Assert.Equal(1, Regex.Matches("try { } catch (ArgumentException ex) { }", @"catch\s*\(\s*[\w\.]+\s+\w").Count);

        // ...and there really ARE catch clauses to constrain.
        Assert.True(Occurrences(stripped, "catch (") >= 1);

        foreach (string token in new[]
                 {
                     ".Message", "StackTrace", "Win32Exception", "GetLastPInvokeErrorMessage",
                     "GetPInvokeErrorMessage", "Marshal.GetLastWin32Error().ToString",
                     "FormatMessage", "throw",
                 })
        {
            Assert.DoesNotContain(token, stripped, StringComparison.Ordinal);
        }
    }

    // ---- no forbidden capability entered the new code -------------------------

    public static TheoryData<string> ForbiddenCapabilityTokens() => new()
    {
        // service control manager - create / change / delete
        "OpenSCManager", "CreateService", "ChangeServiceConfig", "DeleteService",
        "StartService", "ControlService", "ServiceController", "ServiceInstaller",
        "sc.exe", "New-Service", "Set-Service", "Remove-Service",
        // ACL
        "AccessControl", "FileSystemAccessRule", "SetAccessControl", "GetAccessControl",
        "RawSecurityDescriptor", "DiscretionaryAcl", "AddAccessRule", "SetOwner",
        "CryptoKeySecurity", "CryptoKeyRights", "FileSecurity",
        // certificate and private key
        "X509", "CngKey", "CngProvider", "RSACng", "SafeNCryptKeyHandle", "CertificateRequest",
        // registry and credential vault
        "RegistryKey", "Microsoft.Win32.Registry", "CredRead", "CredWrite", "PasswordVault",
        // process start and elevation
        "Process.Start", "ProcessStartInfo", "ShellExecute", "runas", "Verb =",
        // network
        "HttpClient", "WebClient", "HttpRequestMessage", "Socket", "TcpClient", "WebRequest",
        // filesystem and ledger
        "File.", "Directory.", "FileStream", "ownership-ledger",
        // identity impersonation
        "WindowsIdentity", "WindowsImpersonationContext", "LogonUser", "ImpersonateLoggedOnUser",
        // desktop host, React, PAX and Bake
        "PAXCookbook.App", "web-react", "WebView2", "CoreWebView2",
        "PAX_Purview", "PaxEngine", "StartBake", "Start-Bake", "startCook",
    };

    [Theory]
    [MemberData(nameof(ForbiddenCapabilityTokens))]
    public void Neither_new_product_file_contains_a_forbidden_capability(string token)
    {
        foreach (string path in new[] { ResolverPath(), ContractPath() })
        {
            string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(path));

            // POSITIVE CONTROL, per file: the scanner really saw this file's code.
            Assert.False(string.IsNullOrWhiteSpace(code), Path.GetFileName(path) + " produced empty code text");

            Assert.DoesNotContain(token, code, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(ForbiddenCapabilityTokens))]
    public void The_forbidden_capability_scan_can_actually_fire(string token)
    {
        // Without this, "no offenders" would be unfalsifiable.
        Assert.False(string.IsNullOrWhiteSpace(token));

        string synthetic = "class X { void M() { var y = " + token + " ; } }";
        Assert.Contains(token, SetupCSharpLexicalScanner.ExtractCode(synthetic), StringComparison.Ordinal);

        // ...and the scanner is genuinely string-aware in the other direction too.
        Assert.DoesNotContain(
            token,
            SetupCSharpLexicalScanner.ExtractCode("class Y { string s = \"" + token + "\"; }"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            token,
            SetupCSharpLexicalScanner.ExtractCode("// " + token + "\nclass Z { }"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_portable_contract_holds_no_windows_or_native_type()
    {
        string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(ContractPath()));

        // POSITIVE CONTROL.
        Assert.Contains("class ServiceIdentityContract", code, StringComparison.Ordinal);

        foreach (string token in new[]
                 {
                     "SecurityIdentifier", "NTAccount", "DllImport", "LibraryImport", "extern",
                     "Marshal", "SafeHandle", "IntPtr", "LookupAccountName", "Environment.",
                     "System.Security.Principal", "System.Runtime.InteropServices",
                 })
        {
            Assert.DoesNotContain(token, code, StringComparison.Ordinal);
        }

        // The delegation target is the ONE dependency, and it is named explicitly.
        Assert.Contains(
            "ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid",
            code,
            StringComparison.Ordinal);
    }

    // ---- the resolver is caller-unreachable -----------------------------------

    private static readonly string[] ResolverTokens =
    {
        "ServiceSidResolver",
        "ResolveFixedServiceSid",
        "ServiceSidResolution",
        "ServiceSidResolutionState",
        "ServiceSidLookupInterpreter",
        "ServiceSidBufferPlan",
    };

    private static string[] SourceFiles(string root) =>
        Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    // =======================================================================
    // CYCLE 86 (F2) - CALL-SHAPE RESOLVER DETECTION
    // =======================================================================
    //
    // WHY THIS EXISTS. The Setup call-site sweep used to scan for the broad token
    // "ServiceSidResolution", which is a SUBSTRING of the legitimate bounded
    // failure cause ServiceEnableFailureContract.ServiceSidResolutionRefused. That
    // file contains ZERO executable resolver invocations, so the guard was
    // permanently RED on a false positive - and a permanently red guard gives no
    // signal at all, because a genuine violation is indistinguishable from the
    // known noise.
    //
    // WHAT REPLACED IT. A detector for the QUALIFIED INVOCATION shape
    //   ServiceSidResolver . ResolveFixedServiceSid (
    // with arbitrary legal whitespace, including newlines, between the tokens, run
    // over comment-and-string-stripped code. Identifier boundaries on the left mean
    // a longer identifier sharing the prefix cannot match, and the required "." and
    // "(" mean a type mention, a method DECLARATION, an enum member or any other
    // ServiceSidResolution* name cannot match either.
    //
    // THE UNQUALIFIED FORM. ResolveFixedServiceSid() with no qualifier can only
    // compile outside the resolver through a STATIC IMPORT of the resolver type, so
    // that import is itself counted as an invocation site.
    //
    // SCAN ROOT IS DELIBERATELY UNCHANGED: src/PAXCookbookSetup only. The resolver
    // already has a legitimate fully-qualified caller in PAXCookbook.ServiceAdminHelper,
    // which is out of this guard's scope by design. Widening the root would turn
    // that pre-existing authorized call into a new offender.

    private const string ResolverInvocationPattern =
        @"(?<![A-Za-z0-9_])ServiceSidResolver\s*\.\s*ResolveFixedServiceSid\s*\(";

    private static readonly Regex ResolverInvocationRegex =
        new(ResolverInvocationPattern, RegexOptions.CultureInvariant);

    private static readonly Regex ResolverStaticImportRegex =
        new(
            @"using\s+static\s+(?:[A-Za-z0-9_]+\s*\.\s*)*ServiceSidResolver\s*;",
            RegexOptions.CultureInvariant);

    /// <summary>
    /// The ONE detector. The real sweep, every adversarial control and every
    /// mutation control call this exact body, so no control can pass against a
    /// paraphrase of the rule.
    /// </summary>
    internal static int ResolverInvocationSites(string code) =>
        ResolverInvocationRegex.Matches(code).Count
        + ResolverStaticImportRegex.Matches(code).Count;

    // THE ONE EXACT FUTURE CALLER. Whole-canonical-path equality, OrdinalIgnoreCase.
    // No directory, suffix, prefix, wildcard or filename-only allowance.
    // Kept on ONE line: ServiceOwnershipFutureFilePathPinTests pins this exact
    // literal from outside, so a coordinated relocation cannot pass unnoticed.
    private static readonly string[] AuthorizedResolverCallerSegments =
        new[] { "src", "PAXCookbookSetup", "Service", "ServiceOwnershipPromotionExecutor.cs" };

    internal static string AuthorizedResolverCallerPath() =>
        Path.GetFullPath(Path.Combine(RepoRoot(), Path.Combine(AuthorizedResolverCallerSegments)));

    private static readonly Func<string, string, bool> ExactFullPathMatch =
        static (actualFullPath, allowedFullPath) =>
            string.Equals(actualFullPath, allowedFullPath, StringComparison.OrdinalIgnoreCase);

    internal readonly struct ResolverCallScan
    {
        internal ResolverCallScan(int sites, bool authorized)
        {
            Sites = sites;
            Authorized = authorized;
        }

        internal int Sites { get; }

        internal bool Authorized { get; }

        internal bool IsOffender => Sites > 0 && !Authorized;
    }

    // The rule INPUTS (the authorized path and the path matcher) are parameters
    // ONLY so the mutation controls can weaken them without forking the body.
    internal static ResolverCallScan ScanFileForResolverCalls(
        string fileFullPath,
        string code,
        string authorizedCallerFullPath,
        Func<string, string, bool> fileMatches) =>
        new(
            ResolverInvocationSites(code),
            fileMatches(Path.GetFullPath(fileFullPath), authorizedCallerFullPath));

    // THE PRODUCTION RULE.
    internal static ResolverCallScan ScanFileForResolverCalls(string fileFullPath, string code) =>
        ScanFileForResolverCalls(
            fileFullPath, code, AuthorizedResolverCallerPath(), ExactFullPathMatch);

    private static string RelativeToRepo(string fullPath)
    {
        string root = RepoRoot();
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar)
            : fullPath;
    }

    [Fact]
    public void No_setup_source_file_outside_the_resolver_calls_the_resolver()
    {
        // SCAN ROOT - unchanged from cycle 51, and deliberately not widened.
        string scanRoot = Path.Combine(RepoRoot(), "src", "PAXCookbookSetup");
        string[] files = SourceFiles(scanRoot);

        Assert.NotEmpty(files);
        Assert.True(files.Length >= 20, "expected the authored Setup sources, found " + files.Length);

        // POSITIVE CONTROL over the REAL scan root: the scanner is genuinely
        // reading code from these files, so the zero below is calibrated.
        int calibrationHits = 0;
        var offenders = new List<string>();
        int authorizedSites = 0;

        foreach (string f in files)
        {
            if (string.Equals(f, ResolverPath(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(f));
            if (code.Contains("namespace PAXCookbookSetup", StringComparison.Ordinal))
            {
                calibrationHits++;
            }

            ResolverCallScan scan = ScanFileForResolverCalls(f, code);
            if (scan.Sites == 0)
            {
                continue;
            }

            if (scan.Authorized)
            {
                authorizedSites += scan.Sites;
                continue;
            }

            offenders.Add(RelativeToRepo(f) + ":" + scan.Sites);
        }

        Assert.True(
            calibrationHits >= 20,
            "the positive control failed (" + calibrationHits + "), so the zero below is not calibrated");

        Assert.Empty(offenders);

        // FUTURE REQUIRED STATE: at most ONE invocation, and only in the exact
        // authorized executor file. Retained verbatim - the upper bound is the
        // invariant, and cycle 88 does not relax it.
        Assert.True(authorizedSites <= 1, "more than one authorized resolver invocation: " + authorizedSites);

        // CYCLE 88 - EXACT PRESENCE REPLACES TEMPORAL ABSENCE. The authorized
        // executor now exists at its exact pinned path, and it carries EXACTLY ONE
        // qualified invocation. Zero is no longer acceptable: a missing call would
        // mean the executor stopped resolving the fixed service SID, and more than
        // one would mean the single-invocation containment had been lost.
        Assert.True(
            File.Exists(AuthorizedResolverCallerPath()),
            "the authorized executor must exist at its exact pinned path: " + AuthorizedResolverCallerPath());
        Assert.Equal(1, authorizedSites);
    }

    // ---- CYCLE 88: NO SETUP FILE OUTSIDE THE TWO AUTHORIZED ONES MENTIONS IT ---
    //
    // The cycle-86 Critic noted that a purely lexical INVOCATION detector cannot
    // see an alias-qualified call, a METHOD GROUP reference with no "(", or a
    // reflective lookup. This closes all three at once by forbidding the type name
    // from appearing in CODE anywhere under the Setup scan root except the resolver
    // itself and the one authorized executor.

    [Fact]
    public void No_setup_source_file_outside_the_resolver_and_the_executor_mentions_the_resolver()
    {
        string scanRoot = Path.Combine(RepoRoot(), "src", "PAXCookbookSetup");
        string[] files = SourceFiles(scanRoot);
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        int calibrationHits = 0;

        foreach (string f in files)
        {
            if (string.Equals(f, ResolverPath(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    Path.GetFullPath(f), AuthorizedResolverCallerPath(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(f));
            if (code.Contains("namespace PAXCookbookSetup", StringComparison.Ordinal))
            {
                calibrationHits++;
            }

            if (code.Contains("ServiceSidResolver", StringComparison.Ordinal))
            {
                offenders.Add(RelativeToRepo(f));
            }
        }

        Assert.True(
            calibrationHits >= 20,
            "the positive control failed (" + calibrationHits + "), so the zero below is not calibrated");
        Assert.Empty(offenders);

        // BOTH AUTHORIZED FILES REALLY DO MENTION IT, so the zero above is a
        // containment result rather than the scanner failing to find anything.
        Assert.Contains(
            "ServiceSidResolver",
            SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(ResolverPath())),
            StringComparison.Ordinal);
        Assert.Contains(
            "ServiceSidResolver",
            SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(AuthorizedResolverCallerPath())),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// MANDATORY CALIBRATION. Two Setup files mention the resolver in a COMMENT
    /// only. The mention rule above is satisfiable ONLY because it runs over
    /// comment-stripped code, and those comments are legitimate cross-references
    /// that must NOT be edited to dodge a guard. This asserts BOTH directions on
    /// the exact files, so a future change to the comment stripper turns this red
    /// instead of silently making the rule unsatisfiable.
    /// </summary>
    [Theory]
    [InlineData("src/PAXCookbookSetup/Service/ServiceOwnershipCredentialObserver.cs")]
    [InlineData("src/PAXCookbookSetup/Service/ServiceOwnershipLedgerReader.cs")]
    public void The_comment_only_resolver_mentions_are_raw_positive_and_code_zero(string relativePath)
    {
        string full = Path.GetFullPath(Path.Combine(RepoRoot(), Path.Combine(relativePath.Split('/'))));
        Assert.True(File.Exists(full), "the calibration file is missing: " + relativePath);

        string raw = File.ReadAllText(full);
        string code = SetupCSharpLexicalScanner.ExtractCode(raw);

        // RAW text DOES contain it...
        Assert.Contains("ServiceSidResolver", raw, StringComparison.Ordinal);

        // ...and comment-stripped CODE does not.
        Assert.DoesNotContain("ServiceSidResolver", code, StringComparison.Ordinal);

        // ...and it has no invocation either, by the production detector.
        Assert.Equal(0, ResolverInvocationSites(code));
    }

    /// <summary>
    /// The gap the cycle-86 Critic disclosed: a METHOD GROUP reference carries no
    /// "(" and is invisible to the invocation detector, as are an alias-qualified
    /// call and a typeof lookup. The mention rule catches all three even though the
    /// invocation detector does not, and the expected value of BOTH detectors is
    /// stated per case so the division of labour is explicit rather than assumed.
    /// </summary>
    [Theory]
    // source, expected mention, expected invocation sites
    [InlineData("class X { Func<ServiceSidResolution> f = ServiceSidResolver.ResolveFixedServiceSid; }", true, 0)]
    [InlineData("using R = PAXCookbookSetup.Service.ServiceSidResolver;\nclass X { void M() { R.ResolveFixedServiceSid(); } }", true, 0)]
    [InlineData("class X { void M() { var t = typeof(ServiceSidResolver); } }", true, 0)]
    [InlineData("class X { void M() { ServiceSidResolver.ResolveFixedServiceSid(); } }", true, 1)]
    public void The_mention_rule_catches_what_the_invocation_detector_cannot(
        string source, bool expectedMention, int expectedSites)
    {
        string code = SetupCSharpLexicalScanner.ExtractCode(source);

        Assert.Equal(expectedMention, code.Contains("ServiceSidResolver", StringComparison.Ordinal));
        Assert.Equal(expectedSites, ResolverInvocationSites(code));
    }

    /// <summary>
    /// A REFLECTIVE lookup by name lives in a STRING, which the scanner strips, so
    /// neither detector sees it. Disclosed as a measured residual rather than
    /// claimed closed.
    /// </summary>
    [Fact]
    public void A_reflective_lookup_by_name_is_invisible_to_both_detectors()
    {
        string code = SetupCSharpLexicalScanner.ExtractCode(
            "class X { void M() { var t = Type.GetType(\"PAXCookbookSetup.Service.ServiceSidResolver\"); } }");

        Assert.DoesNotContain("ServiceSidResolver", code, StringComparison.Ordinal);
        Assert.Equal(0, ResolverInvocationSites(code));

        // ...and the RAW text does carry it, which is what makes this a disclosed
        // residual rather than an absence.
        Assert.Contains(
            "ServiceSidResolver",
            "class X { void M() { var t = Type.GetType(\"PAXCookbookSetup.Service.ServiceSidResolver\"); } }",
            StringComparison.Ordinal);
    }

    // ---- the detector, in both directions -------------------------------------

    [Theory]
    // The exact false positive that made this guard permanently red.
    [InlineData("enum E { ServiceSidResolutionRefused = 1 }")]
    [InlineData("class X { ServiceSidResolution r; }")]
    [InlineData("class X { ServiceSidResolutionState s; }")]
    [InlineData("class X { ServiceSidLookupInterpreter i; }")]
    [InlineData("class X { ServiceSidBufferPlan p; }")]
    [InlineData("class X { void M() { var t = typeof(ServiceSidResolver); } }")]
    [InlineData("class X { void M() { MyServiceSidResolver.ResolveFixedServiceSid(); } }")]
    [InlineData("class X { void M() { ServiceSidResolver2.ResolveFixedServiceSid(); } }")]
    [InlineData("class X { void M() { ServiceSidResolver.ResolveFixedServiceSidLater(); } }")]
    [InlineData("class X { void M() { ServiceSidResolverHelper.ResolveFixedServiceSid(); } }")]
    // A DECLARATION is not a call.
    [InlineData("static class ServiceSidResolver { internal static ServiceSidResolution ResolveFixedServiceSid() { return default; } }")]
    // A COMMENT is not a call.
    [InlineData("// ServiceSidResolver.ResolveFixedServiceSid();\nclass X { }")]
    [InlineData("/* ServiceSidResolver.ResolveFixedServiceSid(); */ class X { }")]
    // A STRING is not a call.
    [InlineData("class X { string s = \"ServiceSidResolver.ResolveFixedServiceSid()\"; }")]
    [InlineData("class X { string s = @\"ServiceSidResolver.ResolveFixedServiceSid()\"; }")]
    public void The_detector_does_not_fire_on_a_non_invocation(string source)
    {
        Assert.Equal(0, ResolverInvocationSites(SetupCSharpLexicalScanner.ExtractCode(source)));
    }

    [Theory]
    [InlineData("class X { void M() { ServiceSidResolver.ResolveFixedServiceSid(); } }")]
    [InlineData("class X { void M() { ServiceSidResolver . ResolveFixedServiceSid ( ) ; } }")]
    [InlineData("class X { void M() { ServiceSidResolver\n    .ResolveFixedServiceSid\n    (); } }")]
    [InlineData("class X { void M() { ServiceSidResolver\t.\tResolveFixedServiceSid\t(); } }")]
    [InlineData("class X { void M() { PAXCookbookSetup.Service.ServiceSidResolver.ResolveFixedServiceSid(); } }")]
    [InlineData("class X { void M() { var r = ServiceSidResolver.ResolveFixedServiceSid(); } }")]
    // The ONLY way an UNQUALIFIED call could ever compile from outside.
    [InlineData("using static PAXCookbookSetup.Service.ServiceSidResolver;\nclass X { void M() { ResolveFixedServiceSid(); } }")]
    [InlineData("using static ServiceSidResolver;\nclass X { }")]
    public void The_detector_fires_on_every_invocation_spelling(string source)
    {
        Assert.True(ResolverInvocationSites(SetupCSharpLexicalScanner.ExtractCode(source)) >= 1);
    }

    [Fact]
    public void Two_invocations_are_counted_as_two()
    {
        string source =
            "class X { void M() { ServiceSidResolver.ResolveFixedServiceSid(); "
            + "ServiceSidResolver.ResolveFixedServiceSid(); } }";

        Assert.Equal(2, ResolverInvocationSites(SetupCSharpLexicalScanner.ExtractCode(source)));
    }

    // ---- the path allowance, in both directions -------------------------------

    private const string SyntheticInvocation =
        "class X { void M() { ServiceSidResolver.ResolveFixedServiceSid(); } }";

    [Theory]
    // An unrelated Setup file.
    [InlineData("src/PAXCookbookSetup/Service/SomeUnrelatedServiceFile.cs")]
    // The reader file.
    [InlineData("src/PAXCookbookSetup/Service/ServiceOwnershipLedgerReader.cs")]
    // The future writer file.
    [InlineData("src/PAXCookbookSetup/Service/ServiceOwnershipLedgerWriter.cs")]
    // Near-miss executor filenames in the exact authorized directory.
    [InlineData("src/PAXCookbookSetup/Service/ServiceOwnershipPromotionExecutor2.cs")]
    [InlineData("src/PAXCookbookSetup/Service/ServiceOwnershipPromotionExecutor.cs.cs")]
    [InlineData("src/PAXCookbookSetup/Service/_ServiceOwnershipPromotionExecutor.cs")]
    [InlineData("src/PAXCookbookSetup/Service/ServiceOwnershipPromotionExecutorHelper.cs")]
    // The exact same tail, rooted OUTSIDE src.
    [InlineData("tools/PAXCookbookSetup/Service/ServiceOwnershipPromotionExecutor.cs")]
    [InlineData("tests/PAXCookbookSetup/Service/ServiceOwnershipPromotionExecutor.cs")]
    // The exact filename in a different directory under src.
    [InlineData("src/Elsewhere/ServiceOwnershipPromotionExecutor.cs")]
    public void An_invocation_anywhere_but_the_exact_authorized_file_is_an_offender(string relativePath)
    {
        string probe = Path.GetFullPath(
            Path.Combine(RepoRoot(), Path.Combine(relativePath.Split('/'))));
        Assert.NotEqual(AuthorizedResolverCallerPath(), probe, StringComparer.OrdinalIgnoreCase);

        ResolverCallScan scan = ScanFileForResolverCalls(
            probe, SetupCSharpLexicalScanner.ExtractCode(SyntheticInvocation));

        Assert.Equal(1, scan.Sites);
        Assert.False(scan.Authorized);
        Assert.True(scan.IsOffender);
    }

    [Fact]
    public void The_exact_future_executor_path_is_the_one_permitted_invocation_site()
    {
        ResolverCallScan scan = ScanFileForResolverCalls(
            AuthorizedResolverCallerPath(),
            SetupCSharpLexicalScanner.ExtractCode(SyntheticInvocation));

        Assert.Equal(1, scan.Sites);
        Assert.True(scan.Authorized);
        Assert.False(scan.IsOffender);
    }

    [Fact]
    public void Casing_and_separator_variants_still_resolve_the_exact_future_path()
    {
        string code = SetupCSharpLexicalScanner.ExtractCode(SyntheticInvocation);
        string authorized = AuthorizedResolverCallerPath();

        Assert.True(ScanFileForResolverCalls(authorized.ToUpperInvariant(), code).Authorized);
        Assert.True(ScanFileForResolverCalls(authorized.ToLowerInvariant(), code).Authorized);
        Assert.True(ScanFileForResolverCalls(
            authorized.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), code).Authorized);

        // A path that only NORMALIZES to the authorized file is still the same file.
        string viaParent = Path.Combine(
            Path.GetDirectoryName(authorized)!, "sub", "..", Path.GetFileName(authorized));
        Assert.True(ScanFileForResolverCalls(viaParent, code).Authorized);
    }

    [Fact]
    public void More_than_one_invocation_in_the_future_executor_is_still_a_failure()
    {
        string twoCalls =
            "class X { void M() { ServiceSidResolver.ResolveFixedServiceSid(); "
            + "ServiceSidResolver.ResolveFixedServiceSid(); } }";

        ResolverCallScan scan = ScanFileForResolverCalls(
            AuthorizedResolverCallerPath(), SetupCSharpLexicalScanner.ExtractCode(twoCalls));

        Assert.True(scan.Authorized);

        // The sweep's own bound is Sites <= 1, so this configuration would fail it.
        Assert.False(scan.Sites <= 1);
    }

    // ---- mutation controls: the strictness of the rule is load-bearing --------

    [Fact]
    public void MUTATION_a_directory_wide_allowance_would_admit_an_unrelated_setup_file()
    {
        string probe = Path.GetFullPath(Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "Service", "SomeUnrelatedServiceFile.cs"));
        string code = SetupCSharpLexicalScanner.ExtractCode(SyntheticInvocation);

        Assert.True(ScanFileForResolverCalls(probe, code).IsOffender);

        Func<string, string, bool> directoryWideMatch = static (actual, allowed) =>
            string.Equals(
                Path.GetDirectoryName(actual), Path.GetDirectoryName(allowed), StringComparison.OrdinalIgnoreCase);

        Assert.False(ScanFileForResolverCalls(
            probe, code, AuthorizedResolverCallerPath(), directoryWideMatch).IsOffender);
    }

    [Fact]
    public void MUTATION_a_filename_only_allowance_would_admit_the_same_tail_outside_src()
    {
        string probe = Path.GetFullPath(Path.Combine(
            RepoRoot(), "tools", "PAXCookbookSetup", "Service", "ServiceOwnershipPromotionExecutor.cs"));
        string code = SetupCSharpLexicalScanner.ExtractCode(SyntheticInvocation);

        Assert.True(ScanFileForResolverCalls(probe, code).IsOffender);

        Func<string, string, bool> fileNameOnlyMatch = static (actual, allowed) =>
            string.Equals(
                Path.GetFileName(actual), Path.GetFileName(allowed), StringComparison.OrdinalIgnoreCase);

        Assert.False(ScanFileForResolverCalls(
            probe, code, AuthorizedResolverCallerPath(), fileNameOnlyMatch).IsOffender);
    }

    [Fact]
    public void MUTATION_a_prefix_allowance_would_admit_a_near_miss_filename()
    {
        string probe = Path.GetFullPath(Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipPromotionExecutor2.cs"));
        string code = SetupCSharpLexicalScanner.ExtractCode(SyntheticInvocation);

        Assert.True(ScanFileForResolverCalls(probe, code).IsOffender);

        Func<string, string, bool> prefixMatch = static (actual, allowed) =>
            actual.StartsWith(
                allowed.Substring(0, allowed.Length - ".cs".Length), StringComparison.OrdinalIgnoreCase);

        Assert.False(ScanFileForResolverCalls(
            probe, code, AuthorizedResolverCallerPath(), prefixMatch).IsOffender);
    }

    [Fact]
    public void MUTATION_reverting_to_broad_token_matching_reintroduces_the_known_false_positive()
    {
        // The exact file and the exact declaration that kept this guard red.
        string offenderPath = Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceEnableFailureContract.cs");
        Assert.True(File.Exists(offenderPath));

        string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(offenderPath));

        // It really does declare the bounded cause whose name contains the token...
        Assert.Contains("ServiceSidResolutionRefused", code, StringComparison.Ordinal);
        Assert.Contains("ServiceSidResolution", code, StringComparison.Ordinal);

        // ...and it really has ZERO executable resolver invocations.
        Assert.Equal(0, ResolverInvocationSites(code));

        // The old rule fires; the new rule does not. Both directions asserted.
        bool oldRuleWouldFlag = false;
        foreach (string token in ResolverTokens)
        {
            if (code.Contains(token, StringComparison.Ordinal))
            {
                oldRuleWouldFlag = true;
            }
        }

        Assert.True(oldRuleWouldFlag, "the broad-token rule no longer reproduces the known false positive");
        Assert.False(ScanFileForResolverCalls(offenderPath, code).IsOffender);
    }

    [Fact]
    public void The_scan_root_is_still_exactly_the_setup_project()
    {
        // A pre-existing, LEGITIMATE, fully-qualified caller lives in the service
        // admin helper. It is out of this guard's scope by design, and widening the
        // root would turn it into a new offender for a real reason.
        string helper = Path.Combine(
            RepoRoot(), "src", "PAXCookbook.ServiceAdminHelper", "Enable", "ServiceControlManagerAdapter.cs");
        Assert.True(File.Exists(helper));

        string helperCode = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(helper));
        Assert.Equal(1, ResolverInvocationSites(helperCode));

        // ...and it is NOT under the Setup scan root.
        string scanRoot = Path.GetFullPath(Path.Combine(RepoRoot(), "src", "PAXCookbookSetup"));
        Assert.DoesNotContain(
            Path.GetFullPath(helper),
            SourceFiles(scanRoot),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_resolver_file_itself_still_declares_every_resolver_vocabulary_token()
    {
        // Retained from the old rule: the vocabulary really does exist to be found,
        // which is what keeps the desktop/React absence assertions non-vacuous.
        string resolverCode = ResolverCode();
        foreach (string token in ResolverTokens)
        {
            Assert.Contains(token, resolverCode, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Neither_the_desktop_host_nor_the_react_surface_consumes_the_resolver()
    {
        string root = RepoRoot();

        string[] appFiles = SourceFiles(Path.Combine(root, "src", "PAXCookbook.App"));
        Assert.NotEmpty(appFiles);

        foreach (string f in appFiles)
        {
            string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(f));
            foreach (string token in ResolverTokens)
            {
                Assert.DoesNotContain(token, code, StringComparison.Ordinal);
            }
        }

        string reactRoot = Path.Combine(root, "app", "web-react", "src");
        Assert.True(Directory.Exists(reactRoot), "the React source tree was expected to exist");

        string[] reactFiles = Directory
            .GetFiles(reactRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(reactFiles);

        foreach (string f in reactFiles)
        {
            string text = File.ReadAllText(f);
            foreach (string token in ResolverTokens)
            {
                Assert.DoesNotContain(token, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Setup_still_references_shared_and_gained_no_new_project_reference()
    {
        string csproj = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "PAXCookbookSetup.csproj"));

        Assert.Contains(
            @"..\PAXCookbook.Shared\PAXCookbook.Shared.csproj",
            csproj,
            StringComparison.Ordinal);
        Assert.Equal(1, Regex.Matches(csproj, "<ProjectReference").Count);
    }
}

// ===========================================================================
// RULING D1 - TEST-OWNED NATIVE MARSHALLING PROOF
// ===========================================================================
//
// WHAT THIS PROVES. That the documented two-call LookupAccountNameW sequence,
// AS DECLARED IN THE PRODUCT, marshals correctly on this host, and that the
// returned bytes convert through new SecurityIdentifier(bytes, 0).Value into a
// value the portable contract accepts.
//
// WHAT THIS DOES NOT PROVE. It does NOT prove that the future PAX Cookbook
// service account exists, anywhere. NT SERVICE\PAXCookbookService is
// DELIBERATELY NEVER RESOLVED on this host. The only identity resolved here is
// the well-known NT SERVICE\TrustedInstaller, and it is used solely as a
// marshalling fixture.
//
// DISCLOSURE DISCIPLINE. No SID value and no referenced-domain value is printed,
// logged, persisted, concatenated into a message, or placed in evidence. Only
// booleans and bounded states are recorded. That discipline is itself asserted
// structurally at the bottom of this class.
public sealed class ServiceSidResolverNativeMarshallingTests
{
    private const string TrustedInstallerAccount = "NT SERVICE\\TrustedInstaller";

    [DllImport("advapi32.dll", EntryPoint = "LookupAccountNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountNameW(
        string? lpSystemName,
        string lpAccountName,
        byte[]? Sid,
        ref int cbSid,
        char[]? ReferencedDomainName,
        ref int cchReferencedDomainName,
        out int peUse);

    [Fact]
    public void The_documented_two_call_sequence_marshals_and_converts_on_this_host()
    {
        int sidSize = 0;
        int domainChars = 0;

        // The SID_NAME_USE out value is DISCARDED on purpose: correction 4 forbids
        // over-constraining it without official grounding, so it is never read.
        bool firstSucceeded = LookupAccountNameW(
            null, TrustedInstallerAccount, null, ref sidSize, null, ref domainChars, out _);

        // The sizing call must FAIL and report bounded sizes - exactly ruling A1.
        Assert.False(firstSucceeded);
        Assert.True(
            ServiceIdentityContract.IsBoundedSidByteLength(sidSize),
            "NT SERVICE TrustedInstaller did not report a bounded SID size on this host");
        Assert.True(
            ServiceIdentityContract.IsBoundedDomainCharLength(domainChars),
            "NT SERVICE TrustedInstaller did not report a bounded domain size on this host");

        var sidBuffer = new byte[sidSize];
        var domainBuffer = new char[domainChars];
        int secondSidSize = sidSize;
        int secondDomainChars = domainChars;

        bool converted = false;
        bool canonical = false;
        bool acceptedByContract = false;
        bool isAllServicesGroup = true;
        try
        {
            bool secondSucceeded = LookupAccountNameW(
                null, TrustedInstallerAccount, sidBuffer, ref secondSidSize,
                domainBuffer, ref secondDomainChars, out _);

            Assert.True(secondSucceeded);
            Assert.True(secondSidSize > 0 && secondSidSize <= sidBuffer.Length);
            Assert.True(secondDomainChars >= 0 && secondDomainChars <= domainBuffer.Length);

            string resolved = new SecurityIdentifier(sidBuffer, 0).Value;
            converted = resolved.Length > 0;
            canonical = ServiceOwnershipLedgerContract.IsCanonicalSidString(resolved);
            acceptedByContract = ServiceIdentityContract.IsServiceIdentitySid(resolved);
            isAllServicesGroup = string.Equals(resolved, "S-1-5-80-0", StringComparison.Ordinal);
        }
        finally
        {
            Array.Clear(domainBuffer);
        }

        Assert.True(converted);
        Assert.True(canonical);
        Assert.True(acceptedByContract);
        Assert.False(isAllServicesGroup);
    }

    [Fact]
    public void The_product_pinvoke_matches_this_test_declaration_exactly()
    {
        MethodInfo product = typeof(ServiceSidResolver).GetMethod(
            "LookupAccountNameW",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
        MethodInfo mirror = typeof(ServiceSidResolverNativeMarshallingTests).GetMethod(
            "LookupAccountNameW",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.NotNull(product);
        Assert.NotNull(mirror);

        var productImport = product.GetCustomAttribute<DllImportAttribute>();
        var mirrorImport = mirror.GetCustomAttribute<DllImportAttribute>();
        Assert.NotNull(productImport);
        Assert.NotNull(mirrorImport);

        // Absolute expectations, so the comparison cannot be vacuously satisfied by
        // two identically-wrong declarations.
        Assert.Equal("advapi32.dll", productImport!.Value);
        Assert.Equal("LookupAccountNameW", productImport.EntryPoint);
        Assert.Equal(CharSet.Unicode, productImport.CharSet);
        Assert.True(productImport.SetLastError);

        // ...and exact parity with this test's own declaration.
        Assert.Equal(mirrorImport!.Value, productImport.Value);
        Assert.Equal(mirrorImport.EntryPoint, productImport.EntryPoint);
        Assert.Equal(mirrorImport.CharSet, productImport.CharSet);
        Assert.Equal(mirrorImport.SetLastError, productImport.SetLastError);

        // Return marshalling.
        Assert.Equal(typeof(bool), product.ReturnType);
        Assert.Equal(mirror.ReturnType, product.ReturnType);

        var productReturnMarshal = product.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>();
        var mirrorReturnMarshal = mirror.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>();
        Assert.NotNull(productReturnMarshal);
        Assert.NotNull(mirrorReturnMarshal);
        Assert.Equal(UnmanagedType.Bool, productReturnMarshal!.Value);
        Assert.Equal(mirrorReturnMarshal!.Value, productReturnMarshal.Value);

        // Parameter order and types.
        ParameterInfo[] productParams = product.GetParameters();
        ParameterInfo[] mirrorParams = mirror.GetParameters();

        Assert.Equal(7, productParams.Length);
        Assert.Equal(mirrorParams.Length, productParams.Length);

        Type[] expected =
        {
            typeof(string), typeof(string), typeof(byte[]),
            typeof(int).MakeByRefType(), typeof(char[]),
            typeof(int).MakeByRefType(), typeof(int).MakeByRefType(),
        };

        for (int i = 0; i < productParams.Length; i++)
        {
            Assert.Equal(expected[i], productParams[i].ParameterType);
            Assert.Equal(mirrorParams[i].ParameterType, productParams[i].ParameterType);
            Assert.Equal(mirrorParams[i].IsOut, productParams[i].IsOut);
            Assert.Equal(mirrorParams[i].IsIn, productParams[i].IsIn);
        }

        // The LAST parameter is the SID_NAME_USE out value in both declarations.
        Assert.True(productParams[6].IsOut);
        Assert.True(mirrorParams[6].IsOut);
    }

    [Fact]
    public void The_marshalling_proof_never_emits_a_sid_or_a_domain_value()
    {
        string path = Path.Combine(
            ServiceSidResolverStructuralTests.RepoRoot(),
            "tests", "PAXCookbookSetup.Tests", "Service", "ServiceSidResolverTests.cs");
        Assert.True(File.Exists(path), "this test file was expected to exist at its authored path");

        string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(path));
        string body = ServiceSidResolverStructuralTests.MethodBody(
            code, "void The_documented_two_call_sequence_marshals_and_converts_on_this_host()");

        // No sink of any kind.
        foreach (string token in new[]
                 {
                     "Console.", "Trace.", "Debug.", "File.", "Directory.", "Environment.SetEnvironmentVariable",
                     "StreamWriter", "TextWriter", "ITestOutputHelper", "WriteLine",
                 })
        {
            Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        }

        // No string interpolation and no concatenation of the resolved value, so no
        // assertion message can ever carry a SID or a domain name.
        string raw = File.ReadAllText(path);
        string rawBody = ServiceSidResolverStructuralTests.MethodBody(
            raw, "void The_documented_two_call_sequence_marshals_and_converts_on_this_host()");
        Assert.DoesNotContain("$\"", rawBody, StringComparison.Ordinal);
        Assert.DoesNotContain("+ resolved", rawBody, StringComparison.Ordinal);
        Assert.DoesNotContain("resolved +", rawBody, StringComparison.Ordinal);
        Assert.DoesNotContain("+ domainBuffer", rawBody, StringComparison.Ordinal);
        Assert.DoesNotContain("new string(domainBuffer", rawBody, StringComparison.Ordinal);

        // The observable outcome really is only booleans.
        Assert.Contains("Assert.True(converted);", rawBody, StringComparison.Ordinal);
        Assert.Contains("Array.Clear(domainBuffer);", rawBody, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pax_cookbook_service_account_is_never_resolved_by_this_suite()
    {
        // RULING C1, asserted rather than merely promised. No test file in this
        // assembly may hand NT SERVICE\PAXCookbookService, or the contract's
        // qualified name, to a lookup.
        string testRoot = Path.Combine(
            ServiceSidResolverStructuralTests.RepoRoot(), "tests", "PAXCookbookSetup.Tests");

        string[] files = Directory
            .GetFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(files);

        foreach (string f in files)
        {
            string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(f));
            Assert.DoesNotContain("ServiceSidResolver.ResolveFixedServiceSid", code, StringComparison.Ordinal);
            Assert.DoesNotContain("QualifiedServiceAccountName,", code, StringComparison.Ordinal);
            Assert.DoesNotContain("NTAccount", code, StringComparison.Ordinal);
        }

        // POSITIVE CONTROL: the scan DOES see this file's own live lookup token, so
        // it is genuinely reading executable code.
        string ownCode = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(Path.Combine(
            testRoot, "Service", "ServiceSidResolverTests.cs")));
        Assert.Contains("LookupAccountNameW(", ownCode, StringComparison.Ordinal);
        Assert.Contains("TrustedInstallerAccount", ownCode, StringComparison.Ordinal);
    }
}

// ===========================================================================
// STRING-AWARE C# LEXICAL SCANNER (reused from cycle 38b, ruling 7)
// ===========================================================================
//
// A naive stripper is wrong in BOTH directions and both directions matter:
//   FALSE NEGATIVE: var url = "http://x"; new HttpClient();  - cutting at the
//   first "//" throws away the REAL forbidden call that follows.
//   FALSE POSITIVE: @"/* X509Store */" survives a block-comment regex and is
//   then reported as certificate code that does not exist.
// This is a real lexer instead. Comment text and literal CONTENT are removed;
// everything else - including interpolation holes, which really are code -
// survives.
//
// KNOWN LIMITATION, stated rather than hidden: an interpolation hole inside a
// RAW interpolated string has its content stripped with the rest of the literal.
// No such literal exists in the scanned files today, and that error direction
// under-reports, so it is recorded as debt rather than treated as covered.
internal static class SetupCSharpLexicalScanner
{
    public static string ExtractCode(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(source.Length);
        int i = 0;
        int n = source.Length;

        while (i < n)
        {
            char c = source[i];

            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                while (i < n && source[i] != '\n')
                {
                    i++;
                }
                continue;
            }

            if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }
                i = Math.Min(i + 2, n);
                sb.Append(' ');
                continue;
            }

            if (c == '\'')
            {
                i++;
                while (i < n)
                {
                    if (source[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }
                    if (source[i] == '\'')
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                sb.Append(' ');
                continue;
            }

            if (c == '"' || c == '@' || c == '$')
            {
                int j = i;
                bool verbatim = false;
                bool interpolated = false;
                while (j < n && (source[j] == '@' || source[j] == '$'))
                {
                    if (source[j] == '@') { verbatim = true; } else { interpolated = true; }
                    j++;
                }

                if (j < n && source[j] == '"')
                {
                    int run = 0;
                    while (j + run < n && source[j + run] == '"')
                    {
                        run++;
                    }

                    if (run >= 3)
                    {
                        i = SkipRawString(source, j, run);
                        sb.Append(' ');
                        continue;
                    }

                    sb.Append(' ');
                    i = ScanString(source, j + 1, verbatim, interpolated, sb);
                    continue;
                }

                sb.Append(c);
                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static int SkipRawString(string s, int openIndex, int run)
    {
        int i = openIndex + run;
        while (i < s.Length)
        {
            if (s[i] == '"')
            {
                int c = 0;
                while (i + c < s.Length && s[i + c] == '"')
                {
                    c++;
                }
                if (c >= run)
                {
                    return i + c;
                }
                i += c;
                continue;
            }
            i++;
        }
        return s.Length;
    }

    private static int ScanString(string s, int k, bool verbatim, bool interpolated, StringBuilder sb)
    {
        int n = s.Length;
        while (k < n)
        {
            char ch = s[k];

            if (!verbatim && ch == '\\')
            {
                k += 2;
                continue;
            }

            if (ch == '"')
            {
                if (verbatim && k + 1 < n && s[k + 1] == '"')
                {
                    k += 2;
                    continue;
                }
                return k + 1;
            }

            if (interpolated && ch == '{')
            {
                if (k + 1 < n && s[k + 1] == '{')
                {
                    k += 2;
                    continue;
                }
                k = CopyInterpolationHole(s, k + 1, sb);
                continue;
            }

            if (interpolated && ch == '}' && k + 1 < n && s[k + 1] == '}')
            {
                k += 2;
                continue;
            }

            k++;
        }
        return n;
    }

    private static int CopyInterpolationHole(string s, int k, StringBuilder sb)
    {
        int depth = 1;
        sb.Append(' ');
        while (k < s.Length)
        {
            char ch = s[k];
            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    sb.Append(' ');
                    return k + 1;
                }
            }
            sb.Append(ch);
            k++;
        }
        return s.Length;
    }
}
