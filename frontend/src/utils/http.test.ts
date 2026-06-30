import { describe, expect, it } from 'vitest';
import { readJson, SsoRequiredError } from './http';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

async function captureRejection(promise: Promise<unknown>): Promise<SsoRequiredError> {
  try {
    await promise;
    throw new Error('Expected the promise to reject, but it resolved.');
  } catch (err) {
    return err as SsoRequiredError;
  }
}

describe('readJson', () => {
  it('returns the parsed body for a successful response', async () => {
    const result = await readJson<{ value: number }>(jsonResponse({ value: 42 }));
    expect(result).toEqual({ value: 42 });
  });

  it('throws SsoRequiredError when the body reports github_saml_sso_required', async () => {
    const response = jsonResponse(
      {
        code: 'github_saml_sso_required',
        detail: 'Authorize microsoft access to continue.',
        organization: 'microsoft',
        authorizationUrl: 'https://github.com/orgs/microsoft/sso?authorization_request=abc',
      },
      403,
    );

    const error = await captureRejection(readJson(response));
    expect(error).toBeInstanceOf(SsoRequiredError);
    expect(error.organization).toBe('microsoft');
    expect(error.authorizationUrl).toBe(
      'https://github.com/orgs/microsoft/sso?authorization_request=abc',
    );
    expect(error.message).toBe('Authorize microsoft access to continue.');
  });

  it('falls back to a default message when the SSO body omits detail', async () => {
    const response = jsonResponse(
      { code: 'github_saml_sso_required', organization: 'microsoft' },
      403,
    );

    const error = await captureRejection(readJson(response));
    expect(error).toBeInstanceOf(SsoRequiredError);
    expect(error.organization).toBe('microsoft');
    expect(error.authorizationUrl).toBeUndefined();
    expect(error.message).toContain('single sign-on');
  });

  it('throws a plain Error for a non-SSO failure', async () => {
    const response = jsonResponse({ detail: 'Something broke' }, 500);

    const error = await captureRejection(readJson(response));
    expect(error).toBeInstanceOf(Error);
    expect(error).not.toBeInstanceOf(SsoRequiredError);
    expect(error.message).toBe('Something broke');
  });
});
