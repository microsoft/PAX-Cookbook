import { describe, it, expect } from 'vitest';
import { channelDisplayLabel } from './channelDisplay';

// Batch 4 — the customer-facing channel label. The raw internal token
// ('experimental') must NEVER reach a rendered surface; it maps to 'Test'
// ('stable' -> 'Stable'). Every other value passes through untouched, and
// missing/blank input yields null so callers can fall back gracefully.
describe('channelDisplayLabel', () => {
  it('maps the stable token to "Stable"', () => {
    expect(channelDisplayLabel('stable')).toBe('Stable');
  });

  it('maps the experimental token to "Test" and never leaks the raw word', () => {
    const label = channelDisplayLabel('experimental');
    expect(label).toBe('Test');
    expect(label ?? '').not.toMatch(/experimental/i);
  });

  it('is trim/case-insensitive for the known channels', () => {
    expect(channelDisplayLabel('  EXPERIMENTAL ')).toBe('Test');
    expect(channelDisplayLabel('Stable')).toBe('Stable');
  });

  it('passes an unknown non-empty channel through as-is', () => {
    expect(channelDisplayLabel('canary')).toBe('canary');
  });

  it('returns null for missing or blank input', () => {
    expect(channelDisplayLabel(null)).toBeNull();
    expect(channelDisplayLabel(undefined)).toBeNull();
    expect(channelDisplayLabel('')).toBeNull();
    expect(channelDisplayLabel('   ')).toBeNull();
  });
});
