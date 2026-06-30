/**
 * Thrown when the backend reports that GitHub SAML single sign-on (SSO) authorization is required
 * before the signed-in user's organization data can be loaded. Carries the org name and the GitHub
 * authorization URL so the UI can guide the user through resolving it.
 */
export class SsoRequiredError extends Error {
  readonly organization?: string;
  readonly authorizationUrl?: string;

  constructor(message: string, organization?: string, authorizationUrl?: string) {
    super(message);
    this.name = 'SsoRequiredError';
    this.organization = organization;
    this.authorizationUrl = authorizationUrl;
  }
}

export async function readJson<T>(response: Response): Promise<T> {
  if (response.ok) {
    return (await response.json()) as T;
  }

  const payload = await response.json().catch(() => null);

  if (payload?.code === 'github_saml_sso_required') {
    const message =
      typeof payload.detail === 'string'
        ? payload.detail
        : 'GitHub single sign-on authorization is required to load organization data.';
    throw new SsoRequiredError(
      message,
      typeof payload.organization === 'string' ? payload.organization : undefined,
      typeof payload.authorizationUrl === 'string' ? payload.authorizationUrl : undefined,
    );
  }

  const detail =
    typeof payload?.detail === 'string'
      ? payload.detail
      : typeof payload?.title === 'string'
        ? payload.title
        : `HTTP ${response.status}`;
  throw new Error(detail);
}
