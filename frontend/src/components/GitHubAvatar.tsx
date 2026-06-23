import { useEffect, useState } from 'react';
import { initials } from '../utils/format';

type GitHubAvatarProps = {
  login: string | null | undefined;
  size?: number;
  className?: string;
  title?: string;
};

export default function GitHubAvatar({
  login,
  size = 64,
  className,
  title,
}: GitHubAvatarProps) {
  const [errored, setErrored] = useState(false);

  useEffect(() => {
    setErrored(false);
  }, [login]);

  const combinedClassName = ['avatar-dot', className].filter(Boolean).join(' ');

  if (!login || errored) {
    return (
      <span className={combinedClassName} aria-hidden="true" title={title ?? login ?? undefined}>
        {login ? initials(login) : ''}
      </span>
    );
  }

  const encoded = encodeURIComponent(login);
  return (
    <img
      className={combinedClassName}
      src={`https://github.com/${encoded}.png?size=${size}`}
      alt=""
      aria-hidden="true"
      title={title ?? login}
      loading="lazy"
      referrerPolicy="no-referrer"
      onError={() => setErrored(true)}
    />
  );
}
