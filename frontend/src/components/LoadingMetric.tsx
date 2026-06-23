import { useEffect, useState } from 'react';

type LoadingMetricProps<T> = {
  value: T;
  loading: boolean;
  hasLoaded: boolean;
  formatValue: (value: T) => string;
  className?: string;
  placeholder?: string;
  pendingLabel?: string;
};

function LoadingMetric<T>({
  value,
  loading,
  hasLoaded,
  formatValue,
  className,
  placeholder = '—',
  pendingLabel = 'Count is loading',
}: LoadingMetricProps<T>) {
  const [stableValue, setStableValue] = useState(value);

  useEffect(() => {
    if (!loading) {
      setStableValue(value);
    }
  }, [loading, value]);

  const pending = loading && !hasLoaded;
  const displayedValue = loading ? stableValue : value;
  const classNames = [
    'loading-metric',
    className,
    pending ? 'pending' : undefined,
    loading && hasLoaded ? 'refreshing' : undefined,
  ].filter(Boolean).join(' ');

  return (
    <strong
      className={classNames}
      aria-busy={loading || undefined}
      aria-label={pending ? pendingLabel : undefined}
    >
      {pending ? placeholder : formatValue(displayedValue)}
    </strong>
  );
}

export default LoadingMetric;
