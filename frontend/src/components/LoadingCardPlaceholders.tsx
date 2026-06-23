type LoadingCardPlaceholdersProps = {
  count?: number;
  label?: string;
  className?: string;
};

function LoadingCardPlaceholders({
  count = 3,
  label = 'Loading cards',
  className,
}: LoadingCardPlaceholdersProps) {
  return (
    <div
      className={['loading-card-list', className].filter(Boolean).join(' ')}
      aria-label={label}
      role="status"
    >
      {Array.from({ length: count }, (_, index) => (
        <article className="loading-card-placeholder" key={index} aria-hidden="true">
          <span className="loading-card-line short" />
          <span className="loading-card-line title" />
          <span className="loading-card-line medium" />
          <span className="loading-card-pill-row">
            <span />
            <span />
            <span />
          </span>
        </article>
      ))}
    </div>
  );
}

export default LoadingCardPlaceholders;
