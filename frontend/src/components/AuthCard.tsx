import type { AuthStatus, DevelopmentGitHubAccount } from '../types';
import GitHubAvatar from './GitHubAvatar';

type AuthCardProps = {
  authStatus: AuthStatus | null;
  developmentAccounts: DevelopmentGitHubAccount[];
  selectedDevelopmentAccount: string;
  developmentAccountLoading: boolean;
  loginLoading: boolean;
  onLogin: () => void;
  onLogout: () => void;
  onDevelopmentAccountChange: (login: string) => void;
};

function AuthCard({
  authStatus,
  developmentAccounts,
  selectedDevelopmentAccount,
  developmentAccountLoading,
  loginLoading,
  onLogin,
  onLogout,
  onDevelopmentAccountChange,
}: AuthCardProps) {
  const showDevelopmentAccountPicker = developmentAccounts.length > 0 && authStatus?.source !== 'oauth';

  return (
    <div className={`auth-card ${authStatus?.authenticated ? 'ok' : 'warning'}`}>
      <div className="auth-summary">
        <span>{authStatus?.authenticated ? 'Signed in' : 'Auth'}</span>
        {authStatus?.authenticated && authStatus.login && (
          <GitHubAvatar login={authStatus.login} className="auth-avatar" size={48} />
        )}
        <strong>{authStatus?.login ?? authStatus?.source ?? 'GitHub'}</strong>
      </div>
      {showDevelopmentAccountPicker && (
        <label className="dev-account-picker">
          <span>Dev account</span>
          <select
            value={selectedDevelopmentAccount}
            disabled={developmentAccountLoading}
            onChange={(event) => onDevelopmentAccountChange(event.target.value)}
          >
            <option value="">Default token source</option>
            {developmentAccounts.map((account) => (
              <option key={account.login} value={account.login}>
                {account.active ? `${account.login} (active)` : account.login}
              </option>
            ))}
          </select>
        </label>
      )}
      <div className="auth-actions">
        {!authStatus?.authenticated && (
          <button
            type="button"
            onClick={onLogin}
            disabled={!authStatus?.canLogin || loginLoading}
          >
            {loginLoading ? 'Starting...' : 'Sign in'}
          </button>
        )}
        {authStatus?.authenticated && (
          <button type="button" onClick={onLogout}>
            Sign out
          </button>
        )}
      </div>
    </div>
  );
}

export default AuthCard;
