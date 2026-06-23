import { useCallback, useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import type { AuthStatus } from '../types';
import {
  fetchVapidPublicKey,
  getExistingSubscription,
  getPreferences,
  isIos,
  isPushSupported,
  isStandalone,
  savePreferences,
  sendTestNotification,
  subscribeToPush,
  syncSubscription,
  unsubscribeFromPush,
} from '../utils/notifications';

type NotificationSettingsProps = {
  authStatus: AuthStatus | null;
};

type Status = { kind: 'idle' | 'success' | 'error'; text: string };

const idleStatus: Status = { kind: 'idle', text: '' };

function NotificationSettings({ authStatus }: NotificationSettingsProps) {
  const authenticated = authStatus?.authenticated === true;

  const [supported] = useState(isPushSupported);
  const [iosNeedsInstall] = useState(() => isIos() && !isStandalone());
  const [serverKey, setServerKey] = useState<{ publicKey: string; keyId: string } | null>(null);
  const [serverChecked, setServerChecked] = useState(false);
  const [serverEnabled, setServerEnabled] = useState(false);
  const [permission, setPermission] = useState<NotificationPermission>(
    typeof Notification !== 'undefined' ? Notification.permission : 'default',
  );
  const [subscribed, setSubscribed] = useState(false);
  const [reviewRequested, setReviewRequested] = useState(true);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<Status>(idleStatus);

  const canUsePush = authenticated && supported && !iosNeedsInstall;

  // Discover server config + current subscription state once the user is signed in.
  useEffect(() => {
    if (!canUsePush) {
      return;
    }

    let cancelled = false;

    void (async () => {
      try {
        const key = await fetchVapidPublicKey();
        if (cancelled) {
          return;
        }

        setServerChecked(true);
        if (!key) {
          setServerEnabled(false);
          return;
        }

        setServerEnabled(true);
        setServerKey(key);

        const existing = await getExistingSubscription();
        if (cancelled) {
          return;
        }

        if (existing) {
          try {
            await syncSubscription(key);
          } catch {
            // Keep the existing subscription if the refresh fails; it may still be valid.
          }
          if (cancelled) {
            return;
          }

          setSubscribed(true);
          try {
            const prefs = await getPreferences();
            if (!cancelled) {
              setReviewRequested(prefs.reviewRequested);
            }
          } catch {
            // Non-fatal: leave the default toggle state.
          }
        }
      } catch {
        if (!cancelled) {
          setServerChecked(true);
          setServerEnabled(false);
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [canUsePush]);

  const enable = useCallback(async () => {
    if (!serverKey) {
      return;
    }

    setBusy(true);
    setStatus(idleStatus);
    try {
      const result = await Notification.requestPermission();
      setPermission(result);
      if (result !== 'granted') {
        setStatus({ kind: 'error', text: 'Notification permission was not granted.' });
        return;
      }

      await subscribeToPush(serverKey);
      setSubscribed(true);
      try {
        const prefs = await getPreferences();
        setReviewRequested(prefs.reviewRequested);
      } catch {
        // Ignore; defaults apply.
      }
      setStatus({ kind: 'success', text: 'Notifications enabled on this device.' });
    } catch (error) {
      setStatus({ kind: 'error', text: error instanceof Error ? error.message : 'Could not enable notifications.' });
    } finally {
      setBusy(false);
    }
  }, [serverKey]);

  const disable = useCallback(async () => {
    setBusy(true);
    setStatus(idleStatus);
    try {
      await unsubscribeFromPush();
      setSubscribed(false);
      setStatus({ kind: 'success', text: 'Notifications turned off on this device.' });
    } catch (error) {
      setStatus({ kind: 'error', text: error instanceof Error ? error.message : 'Could not turn off notifications.' });
    } finally {
      setBusy(false);
    }
  }, []);

  const toggleReviewRequested = useCallback(async () => {
    const next = !reviewRequested;
    setReviewRequested(next);
    setBusy(true);
    setStatus(idleStatus);
    try {
      const saved = await savePreferences({ reviewRequested: next });
      setReviewRequested(saved.reviewRequested);
    } catch (error) {
      setReviewRequested(!next);
      setStatus({ kind: 'error', text: error instanceof Error ? error.message : 'Could not save preference.' });
    } finally {
      setBusy(false);
    }
  }, [reviewRequested]);

  const test = useCallback(async () => {
    setBusy(true);
    setStatus(idleStatus);
    try {
      const result = await sendTestNotification();
      setStatus(
        result.sent > 0
          ? { kind: 'success', text: 'Test notification sent.' }
          : { kind: 'error', text: 'No device received the test. Try re-enabling notifications.' },
      );
    } catch (error) {
      setStatus({ kind: 'error', text: error instanceof Error ? error.message : 'Could not send a test notification.' });
    } finally {
      setBusy(false);
    }
  }, []);

  let body: ReactNode;
  if (!authenticated) {
    body = <p className="notif-hint">Sign in to get notified when a pull request needs your review.</p>;
  } else if (!supported) {
    body = <p className="notif-hint">This browser doesn’t support push notifications.</p>;
  } else if (iosNeedsInstall) {
    body = (
      <p className="notif-hint">
        On iPhone or iPad, add this app to your Home Screen (Share → Add to Home Screen), then open it
        from there to enable notifications.
      </p>
    );
  } else if (serverChecked && !serverEnabled) {
    body = <p className="notif-hint">Push notifications aren’t configured on this server yet.</p>;
  } else if (permission === 'denied') {
    body = (
      <p className="notif-hint">
        Notifications are blocked for this site. Allow them in your browser settings, then reload.
      </p>
    );
  } else if (!subscribed) {
    body = (
      <div className="notif-actions">
        <button type="button" onClick={() => void enable()} disabled={busy || !serverKey}>
          {busy ? 'Enabling…' : 'Enable notifications'}
        </button>
      </div>
    );
  } else {
    body = (
      <div className="notif-controls">
        <label className="notif-toggle">
          <input
            type="checkbox"
            checked={reviewRequested}
            onChange={() => void toggleReviewRequested()}
            disabled={busy}
          />
          <span>Review requested</span>
        </label>
        <div className="notif-actions">
          <button type="button" onClick={() => void test()} disabled={busy}>
            Send test
          </button>
          <button type="button" className="notif-secondary" onClick={() => void disable()} disabled={busy}>
            Turn off on this device
          </button>
        </div>
      </div>
    );
  }

  return (
    <section className="notif-panel" aria-label="Notification settings">
      <div className="notif-header">
        <span className="notif-title">Notifications</span>
      </div>
      {body}
      {status.kind !== 'idle' && (
        <p className={`notif-status ${status.kind}`} role={status.kind === 'error' ? 'alert' : 'status'}>
          {status.text}
        </p>
      )}
    </section>
  );
}

export default NotificationSettings;
