using System;
using System.Linq;
using System.Reflection;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Shared.Tests;

// ===========================================================================
// CYCLE 39 - FIXED SERVICE IDENTITY CONTRACT
// ===========================================================================
//
// SCOPE, stated plainly. ServiceIdentityContract is a PURE, PORTABLE contract:
// bounded compile-time constants plus pure predicates. It opens no certificate
// store, touches no key, reads or writes no permission list, reads no registry,
// reads no credential vault, composes no path, starts no process, opens no
// socket, performs no file access, and performs NO NATIVE LOOKUP. It cannot
// resolve a SID; it can only VALIDATE text that something else produced.
//
// WHAT THIS FILE DOES NOT PROVE. Nothing here proves that any account named
// "NT SERVICE\PAXCookbookService" exists on any machine, or that it could be
// resolved. This cycle deliberately never resolves that name on this host.
//
// DELEGATION (ruling E1). Service-SID SHAPE is NOT re-implemented here. It is
// delegated to ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid, which
// is already compile-linked into the service. The parity tests below prove the
// delegation is exact rather than merely similar, so the two can never drift.
public class ServiceIdentityContractTests
{
    // =======================================================================
    // FIXED CONSTANTS
    // =======================================================================

    [Fact]
    public void The_fixed_identity_constants_are_exact()
    {
        Assert.Equal("PAXCookbookService", ServiceIdentityContract.ServiceName);
        Assert.Equal("PAXCookbook Machine Service", ServiceIdentityContract.ServiceDisplayName);
        Assert.Equal("NT SERVICE", ServiceIdentityContract.ServiceAccountDomain);
        Assert.Equal("\\", ServiceIdentityContract.AccountDomainSeparator);
    }

    [Fact]
    public void The_qualified_account_name_is_the_composition_of_domain_separator_and_name()
    {
        // The COMPOSITION itself is asserted, not just the final text: a future
        // edit that changed the service name but hand-typed the qualified name
        // would break here.
        Assert.Equal(
            ServiceIdentityContract.ServiceAccountDomain
                + ServiceIdentityContract.AccountDomainSeparator
                + ServiceIdentityContract.ServiceName,
            ServiceIdentityContract.QualifiedServiceAccountName);

        // And the exact literal text, so the composition cannot be "correct"
        // while both halves drifted together.
        Assert.Equal("NT SERVICE\\PAXCookbookService", ServiceIdentityContract.QualifiedServiceAccountName);

        // Exactly one separator, and it is a single backslash.
        Assert.Equal(1, ServiceIdentityContract.AccountDomainSeparator.Length);
        Assert.Equal('\\', ServiceIdentityContract.AccountDomainSeparator[0]);
        Assert.Equal(1, ServiceIdentityContract.QualifiedServiceAccountName.Count(c => c == '\\'));
    }

    [Fact]
    public void The_fixed_names_contain_no_separator_traversal_or_wildcard()
    {
        foreach (string value in new[]
                 {
                     ServiceIdentityContract.ServiceName,
                     ServiceIdentityContract.ServiceDisplayName,
                 })
        {
            Assert.False(string.IsNullOrWhiteSpace(value));
            Assert.DoesNotContain("..", value, StringComparison.Ordinal);
            Assert.DoesNotContain("/", value, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", value, StringComparison.Ordinal);
            Assert.DoesNotContain(":", value, StringComparison.Ordinal);
            Assert.DoesNotContain("*", value, StringComparison.Ordinal);
            Assert.DoesNotContain("%", value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_bounded_size_constants_are_positive_and_bounded()
    {
        Assert.True(ServiceIdentityContract.MinSidByteLength > 0);
        Assert.True(ServiceIdentityContract.MaxSidByteLength > ServiceIdentityContract.MinSidByteLength);
        Assert.True(ServiceIdentityContract.MaxSidByteLength <= 4096);
        Assert.True(ServiceIdentityContract.MaxDomainNameCharLength > 0);
        Assert.True(ServiceIdentityContract.MaxDomainNameCharLength <= 4096);
        Assert.True(ServiceIdentityContract.MaxAccountNameCharLength
            >= ServiceIdentityContract.QualifiedServiceAccountName.Length);
        Assert.True(ServiceIdentityContract.MaxAccountNameCharLength <= 4096);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(int.MinValue, false)]
    public void A_sid_byte_length_is_accepted_only_inside_the_documented_bound(int length, bool expected)
    {
        Assert.Equal(expected, ServiceIdentityContract.IsBoundedSidByteLength(length));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(int.MinValue, false)]
    public void A_domain_char_length_is_accepted_only_inside_the_documented_bound(int length, bool expected)
    {
        Assert.Equal(expected, ServiceIdentityContract.IsBoundedDomainCharLength(length));
    }

    // =======================================================================
    // NO LOCALIZED, CONFIGURABLE, OR REDIRECTABLE NAME
    // =======================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("paxcookbookservice")]        // case variation
    [InlineData("PAXCOOKBOOKSERVICE")]
    [InlineData("PAXCookbookService ")]       // trailing space
    [InlineData(" PAXCookbookService")]
    [InlineData("PAXCookbookService2")]
    [InlineData("PAXCookbook Machine Service")]
    [InlineData("NT SERVICE\\PAXCookbookService")]
    public void A_name_that_is_not_the_exact_fixed_service_name_is_refused(string? candidate)
    {
        Assert.False(ServiceIdentityContract.IsFixedServiceName(candidate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pax cookbook machine service")]
    [InlineData("PAXCookbook  Machine Service")]  // doubled space
    [InlineData("PAXCookbookService")]
    [InlineData("Service PAXCookbook Machine")]
    public void A_name_that_is_not_the_exact_fixed_display_name_is_refused(string? candidate)
    {
        Assert.False(ServiceIdentityContract.IsFixedServiceDisplayName(candidate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PAXCookbookService")]                    // unqualified
    [InlineData("NT SERVICE\\paxcookbookservice")]        // case variation
    [InlineData("nt service\\PAXCookbookService")]
    [InlineData("NT AUTHORITY\\PAXCookbookService")]      // wrong domain
    [InlineData("NT SERVICE/PAXCookbookService")]         // wrong separator
    [InlineData("NT SERVICE\\\\PAXCookbookService")]      // doubled separator
    [InlineData(".\\PAXCookbookService")]
    [InlineData("SERVICE NT\\PAXCookbookService")]
    [InlineData("Dienst\\PAXCookbookService")]            // localized domain
    [InlineData("S-1-5-80-1")]                            // a SID is not a name
    public void A_name_that_is_not_the_exact_qualified_account_name_is_refused(string? candidate)
    {
        Assert.False(ServiceIdentityContract.IsFixedQualifiedServiceAccountName(candidate));
    }

    [Fact]
    public void The_three_exact_fixed_names_are_accepted()
    {
        // POSITIVE CONTROL for every refusal above. Without this, the predicates
        // could be constant-false and the whole block would still pass.
        Assert.True(ServiceIdentityContract.IsFixedServiceName(ServiceIdentityContract.ServiceName));
        Assert.True(ServiceIdentityContract.IsFixedServiceDisplayName(ServiceIdentityContract.ServiceDisplayName));
        Assert.True(ServiceIdentityContract.IsFixedQualifiedServiceAccountName(
            ServiceIdentityContract.QualifiedServiceAccountName));
    }

    [Fact]
    public void The_contract_exposes_no_writable_or_redirectable_identity_state()
    {
        Type t = typeof(ServiceIdentityContract);

        // A static class: no instance, no injected state.
        Assert.True(t.IsAbstract && t.IsSealed);
        Assert.Empty(t.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        // Every field is a compile-time literal or read-only static. Nothing can
        // be assigned at run time, so no environment variable, configuration
        // file, command line, or machine key can redirect the identity.
        foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            Assert.True(f.IsLiteral || f.IsInitOnly, f.Name + " is neither const nor readonly");
        }
        Assert.Empty(t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

        // No settable property.
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            Assert.False(p.CanWrite, p.Name + " is settable");
        }

        // No delegate-shaped member: the contract cannot become a channel for
        // arbitrary behaviour.
        foreach (MemberInfo m in t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (m is FieldInfo fi)
            {
                Assert.False(typeof(Delegate).IsAssignableFrom(fi.FieldType), fi.Name + " is a delegate");
            }
            if (m is PropertyInfo pi)
            {
                Assert.False(typeof(Delegate).IsAssignableFrom(pi.PropertyType), pi.Name + " is a delegate");
            }
        }

        // Every declared public method is a PURE PREDICATE: it returns bool and
        // therefore can never HAND OUT a name that differs from the constants.
        MethodInfo[] declared = t
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .ToArray();
        Assert.NotEmpty(declared);
        foreach (MethodInfo m in declared)
        {
            Assert.Equal(typeof(bool), m.ReturnType);
        }
    }

    // =======================================================================
    // SERVICE SID SHAPE - DELEGATED, AND PROVEN AT PARITY WITH CYCLE 38a
    // =======================================================================

    public static TheoryData<string?> AcceptedServiceSids() => new()
    {
        "S-1-5-80-1",
        "S-1-5-80-0-1",
        "S-1-5-80-1-2",
        "S-1-5-80-1-2-3-4-5-6",
        "S-1-5-80-3139157870-2983391045-3678747466-658725712-1809340420",
        "S-1-5-80-4294967295",
    };

    public static TheoryData<string?> RejectedServiceSids() => new()
    {
        null,
        "",
        " ",
        "S-1-5-80-0",                                        // NT SERVICE\ALL SERVICES
        "S-1-5-80",                                          // no subauthority after 80
        "S-1-5-21-1111111111-2222222222-3333333333-1001",    // a user, not a service
        "S-1-5-18",
        "S-1-5-19",
        "S-1-5-20",
        "S-1-5-32-544",
        "S-1-1-0",
        "S-1-5-11",
        "S-1-5-800-1",                                       // not the 80 prefix
        "S-1-6-80-1",                                        // wrong identifier authority
        "S-2-5-80-1",                                        // wrong revision
        "X-1-5-80-1",
        "s-1-5-80-1",                                        // lower case is not canonical
        "S-1-5-080-1",                                       // leading zero
        "S-1-5-80-01",                                       // leading zero
        "S-1-5-80-4294967296",                               // subauthority overflow
        "S-1-281474976710656-80-1",                          // identifier authority overflow
        " S-1-5-80-1",                                       // leading whitespace
        "S-1-5-80-1 ",                                       // trailing whitespace
        "S-1-5-80-1-",                                       // empty trailing component
        "S-1-5--80-1",                                       // empty component
        "S-1-5-80-1-2-3-4-5-6-7-8-9-10-11-12-13-14-15-16",   // too many subauthorities
        "not-a-sid",
        "NT SERVICE\\PAXCookbookService",                    // a localized name is never a SID
        "NT SERVICE\\ALL SERVICES",
        "TrustedInstaller",
    };

    [Theory]
    [MemberData(nameof(AcceptedServiceSids))]
    public void A_bounded_per_service_sid_is_accepted(string? sid)
    {
        Assert.True(ServiceIdentityContract.IsServiceIdentitySid(sid));
    }

    [Theory]
    [MemberData(nameof(RejectedServiceSids))]
    public void A_sid_that_is_not_a_bounded_per_service_sid_is_refused(string? sid)
    {
        Assert.False(ServiceIdentityContract.IsServiceIdentitySid(sid));
    }

    [Theory]
    [MemberData(nameof(AcceptedServiceSids))]
    public void An_accepted_sid_is_at_parity_with_the_cycle_38a_ledger_contract(string? sid)
    {
        // RULING E1.5. Not "similar": IDENTICAL, case by case.
        Assert.Equal(
            ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid(sid),
            ServiceIdentityContract.IsServiceIdentitySid(sid));
        Assert.True(ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid(sid));
    }

    [Theory]
    [MemberData(nameof(RejectedServiceSids))]
    public void A_rejected_sid_is_at_parity_with_the_cycle_38a_ledger_contract(string? sid)
    {
        Assert.Equal(
            ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid(sid),
            ServiceIdentityContract.IsServiceIdentitySid(sid));
        Assert.False(ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid(sid));
    }

    [Fact]
    public void The_accepted_set_deliberately_varies_the_subauthority_count()
    {
        // The point of the accepted set: no exact subauthority count is asserted
        // anywhere, because none is documented for a per-service virtual account
        // SID. Asserting one would be an invention.
        int[] counts = AcceptedServiceSids()
            .Select(row => (string)row[0]!)
            .Select(s => s.Split('-').Length - 3)
            .Distinct()
            .OrderBy(n => n)
            .ToArray();

        Assert.True(counts.Length >= 4, "expected several distinct subauthority counts, got " + counts.Length);

        // The minimum accepted shape carries exactly two subauthorities (80 plus
        // one more); the set also carries much longer ones.
        Assert.Contains(2, counts);
        Assert.True(counts.Max() >= 6);
    }

    [Fact]
    public void The_all_services_group_sid_is_refused_specifically()
    {
        // Called out on its own because it is the one SID that is shaped exactly
        // like a service SID and yet grants the WHOLE services group.
        Assert.False(ServiceIdentityContract.IsServiceIdentitySid("S-1-5-80-0"));
        Assert.True(ServiceOwnershipLedgerContract.IsCanonicalSidString("S-1-5-80-0"));

        // POSITIVE CONTROL: the very next SID in the family IS accepted, so the
        // refusal above is specific rather than a blanket prefix rejection.
        Assert.True(ServiceIdentityContract.IsServiceIdentitySid("S-1-5-80-1"));
    }
}
