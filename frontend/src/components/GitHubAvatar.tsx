import { useState } from 'react';
import { initials } from '../utils/format';

type GitHubAvatarProps = {
  login: string;
  size?: number;
  className?: string;
  title?: string;
};

export default function GitHubAvatar({
  login,
  size = 64,
  className = 'avatar-dot',
  title,
}: GitHubAvatarProps) {
  const [errored, setErrored] = useState(false);

  if (!login || errored) {
    return (
      <span className={className} title={title ?? login} aria-hidden={!login}>
        {login ? initials(login) : ''}
      </span>
    );
  }

  const encoded = encodeURIComponent(login);
  return (
    <img
      className={className}
      src={`https://github.com/${encoded}.png?size=${size}`}
      alt=""
      title={title ?? login}
      loading="lazy"
      onError={() => setErrored(true)}
    />
  );
}
