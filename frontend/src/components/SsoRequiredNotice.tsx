import type { SsoRequiredInfo } from '../types';

type SsoRequiredNoticeProps = {
  info: SsoRequiredInfo;
  onReload: () => void;
};

function buildAuthorizeUrl(info: SsoRequiredInfo): string {
  if (info.authorizationUrl) {
    return info.authorizationUrl;
  }

  if (info.organization) {
    return `https://github.com/orgs/${encodeURIComponent(info.organization)}/sso`;
  }

  return 'https://github.com/settings/connections/applications';
}

function SsoRequiredNotice({ info, onReload }: SsoRequiredNoticeProps) {
  const orgLabel = info.organization ?? 'your organization';
  const authorizeUrl = buildAuthorizeUrl(info);

  return (
    <section className="sso-required" role="alert" aria-live="assertive">
      <div className="sso-required-icon" aria-hidden="true">!</div>
      <div className="sso-required-body">
        <h2 className="sso-required-title">Authorize {orgLabel} access on GitHub</h2>
        <p className="sso-required-message">{info.message}</p>
        <p className="sso-required-detail">
          You&apos;re signed in, but GitHub requires single sign-on (SSO) authorization before this app
          can load {orgLabel} data. Authorize access on GitHub, then come back and reload.
        </p>
        <div className="sso-required-actions">
          <a
            className="sso-required-button primary"
            href={authorizeUrl}
            target="_blank"
            rel="noreferrer"
          >
            Authorize {orgLabel} access
          </a>
          <button type="button" className="sso-required-button" onClick={onReload}>
            I&apos;ve authorized — reload
          </button>
        </div>
      </div>
    </section>
  );
}

export default SsoRequiredNotice;
