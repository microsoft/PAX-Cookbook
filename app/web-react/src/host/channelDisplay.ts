// Customer-visible release-channel display mapping.
//
// The runtime reports the internal channel token verbatim ('stable' /
// 'experimental'). That raw token must never be shown to a business user, so
// every customer-visible surface (Settings "App information", Updates support
// details, the copied support text) maps it through this helper instead:
//
//   'stable'        -> 'Stable'
//   'experimental'  -> 'Test'
//
// Any other non-empty value is shown as-is (a future channel is never hidden);
// an empty/missing value returns null so callers can fall back to their own
// "Not reported" copy. The internal token is unchanged — this is display only.
export function channelDisplayLabel(
  raw: string | null | undefined,
): string | null {
  if (typeof raw !== 'string') {
    return null;
  }
  const trimmed = raw.trim();
  if (trimmed === '') {
    return null;
  }
  switch (trimmed.toLowerCase()) {
    case 'stable':
      return 'Stable';
    case 'experimental':
      return 'Test';
    default:
      return trimmed;
  }
}
