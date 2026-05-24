import type { TeamMetrics } from '../../types';

type TeamMetricsStripProps = {
  metrics: TeamMetrics;
};

function TeamMetricsStrip({ metrics }: TeamMetricsStripProps) {
  return (
    <section className="pace-strip" aria-label="Team flow metrics">
      <div>
        <span>Readiness ↑</span>
        <strong>{metrics.averageReadiness}%</strong>
      </div>
      <div>
        <span>Review coverage ↑</span>
        <strong>{metrics.reviewCoverage}%</strong>
      </div>
      <div>
        <span>Waiting ↓</span>
        <strong>{metrics.waiting}</strong>
      </div>
      <div>
        <span>Idle ↓</span>
        <strong>{metrics.idle}</strong>
      </div>
    </section>
  );
}

export default TeamMetricsStrip;
