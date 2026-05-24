import type { AuthStatus } from '../types';

type AuthCardProps = {
  authStatus: AuthStatus | null;
  loginLoading: boolean;
  onLogin: () => void;
  onLogout: () => void;
};

function AuthCard({ authStatus, loginLoading, onLogin, onLogout }: AuthCardProps) {
  return (
    <div className={`auth-card ${authStatus?.authenticated ? 'ok' : 'warning'}`}>
      <div className="auth-summary">
        <span>{authStatus?.authenticated ? 'Signed in' : 'Auth'}</span>
        <strong>{authStatus?.login ?? authStatus?.source ?? 'GitHub'}</strong>
      </div>
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
