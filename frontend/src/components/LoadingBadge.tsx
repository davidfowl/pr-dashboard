type LoadingBadgeProps = {
  label?: string;
  ariaLabel?: string;
  className?: string;
};

function LoadingBadge({ label = 'Loading', ariaLabel, className }: LoadingBadgeProps) {
  const classNames = ['loading-badge', className].filter(Boolean).join(' ');

  return (
    <span className={classNames} aria-label={ariaLabel ?? label}>
      <span aria-hidden="true" />
      {label}
    </span>
  );
}

export default LoadingBadge;
