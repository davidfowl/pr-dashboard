import { describe, expect, it } from 'vitest';
import { buildPullRequestDeepLink, urlBase64ToUint8Array } from './notifications';
import { parseDetailHash } from './routing';

describe('notifications helpers', () => {
  it('builds a deep link that the app router can parse', () => {
    const link = buildPullRequestDeepLink('microsoft/aspire', 101);
    expect(link).toBe('/#pr/microsoft%2Faspire/101');

    // The SW navigates to this URL; window.location.hash is the part after the slash.
    const parsed = parseDetailHash('#pr/microsoft%2Faspire/101');
    expect(parsed).toEqual({ repository: 'microsoft/aspire', number: 101 });
  });

  it('round-trips a repo name containing characters that need escaping', () => {
    const link = buildPullRequestDeepLink('dotnet/runtime', 42);
    const hash = link.slice(1); // drop the leading '/'
    expect(parseDetailHash(hash)).toEqual({ repository: 'dotnet/runtime', number: 42 });
  });

  it('converts a base64url VAPID key into the expected byte length', () => {
    // 65-byte uncompressed P-256 public key encodes to 87 base64url chars.
    const key = 'B'.repeat(87);
    const bytes = urlBase64ToUint8Array(key);
    expect(bytes).toBeInstanceOf(Uint8Array);
    expect(bytes.length).toBe(65);
  });

  it('decodes base64url (- and _) without throwing', () => {
    const bytes = urlBase64ToUint8Array('a-_b');
    expect(bytes.length).toBeGreaterThan(0);
  });
});
