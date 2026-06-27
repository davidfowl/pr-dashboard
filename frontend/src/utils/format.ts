const invariantCountLabels = new Set(['shown']);

export function formatCount(count: number, singular: string, plural = `${singular}s`) {
  const label = count === 1 || invariantCountLabels.has(singular) ? singular : plural;
  return `${count.toLocaleString()} ${label}`;
}

export function formatRelative(value: string) {
  const date = new Date(value);
  const diffSeconds = Math.max(0, Math.round((Date.now() - date.getTime()) / 1000));
  if (diffSeconds < 60) {
    return 'just now';
  }

  const diffMinutes = Math.round(diffSeconds / 60);
  if (diffMinutes < 60) {
    return `${diffMinutes}m ago`;
  }

  const diffHours = Math.round(diffMinutes / 60);
  if (diffHours < 24) {
    return `${diffHours}h ago`;
  }

  const diffDays = Math.round(diffHours / 24);
  return `${diffDays}d ago`;
}

export function formatAge(value: string) {
  return formatRelative(value).replace(' ago', '');
}

export function formatDateShort(value: string) {
  return new Date(value).toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
  });
}

export function formatTime(value: string) {
  return new Date(value).toLocaleTimeString(undefined, {
    hour: 'numeric',
    minute: '2-digit',
  });
}

export function formatDuration(value?: number) {
  if (value === undefined || value === null) {
    return 'n/a';
  }

  const totalMinutes = Math.max(0, Math.round(value / 1000 / 60));
  if (totalMinutes < 60) {
    return `${totalMinutes}m`;
  }

  const totalHours = Math.round(totalMinutes / 60);
  if (totalHours < 48) {
    return `${totalHours}h`;
  }

  const days = Math.floor(totalHours / 24);
  const hours = totalHours % 24;
  return hours === 0 ? `${days}d` : `${days}d ${hours}h`;
}

export function colorForText(value: string) {
  let hash = 0;
  for (const char of value) {
    hash = (hash * 31 + char.charCodeAt(0)) >>> 0;
  }

  const hue = hash % 360;
  return `hsl(${hue} 68% 58%)`;
}

export function initials(value: string) {
  return value
    .split(/[\s-]+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase())
    .join('');
}

export function truncate(value: string, length: number) {
  return value.length > length ? `${value.slice(0, length)}...` : value;
}
