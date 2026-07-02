import { useEffect, useState } from 'react';
import type { AppInfoResponse } from '../types';
import { readJson } from '../utils/http';

function AppInfo() {
  const [appInfo, setAppInfo] = useState<AppInfoResponse>({
    commitSha: 'local',
    shortCommitSha: 'local',
  });

  useEffect(() => {
    async function loadAppInfo() {
      try {
        const response = await fetch('/api/app-info');
        setAppInfo(await readJson<AppInfoResponse>(response));
      } catch {
        setAppInfo({
          commitSha: 'local',
          shortCommitSha: 'local',
        });
      }
    }

    void loadAppInfo();
  }, []);

  return (
    <footer className="site-footer" aria-label="Build information">
      <a href="/api/agents/schema" target="_blank" rel="noreferrer">
        Agent schema
      </a>
      <span aria-hidden="true">·</span>
      <span>Commit</span>
      {appInfo.commitUrl ? (
        <a href={appInfo.commitUrl} target="_blank" rel="noreferrer">
          {appInfo.shortCommitSha}
        </a>
      ) : (
        <code>{appInfo.shortCommitSha}</code>
      )}
    </footer>
  );
}

export default AppInfo;
