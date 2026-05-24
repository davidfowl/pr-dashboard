import type { CSSProperties } from 'react';
import type { ActivityModel } from '../../types';

type ActivityPanelProps = {
  model: ActivityModel;
};

function ActivityPanel({ model }: ActivityPanelProps) {
  return (
    <section className="activity-panel" aria-label="Pull request activity">
      <div className="section-title-row">
        <p className="eyebrow">Activity</p>
        <h3>Where time went</h3>
      </div>
      <div className="activity-signals">
        {model.signals.map((signal) => (
          <span key={signal.label} className={`attention-signal ${signal.tone ?? 'muted'}`}>
            {signal.label}
          </span>
        ))}
      </div>
      <div className="activity-legend" aria-hidden="true">
        <span className="commit">Commit</span>
        <span className="review">Review</span>
        <span className="approval">Approval</span>
        <span className="changes">Changes</span>
        <span className="comment">Comment</span>
        <span className="bot">Bot</span>
      </div>
      <div className="activity-strip">
        <div className="activity-rail" />
        {model.gaps.map((gap) => (
          <span
            key={gap.id}
            className="activity-gap"
            style={{ '--gap-left': `${gap.left}%`, '--gap-width': `${gap.width}%` } as CSSProperties}
          >
            {gap.label}
          </span>
        ))}
        {model.markers.map((marker) => (
          <span
            key={marker.id}
            className={`activity-marker ${marker.tone}`}
            style={{ '--marker-left': `${marker.left}%` } as CSSProperties}
            title={marker.title}
            aria-label={marker.title}
          />
        ))}
      </div>
      <div className="activity-range">
        <span>{model.startLabel}</span>
        <span>{model.endLabel}</span>
      </div>
    </section>
  );
}

export default ActivityPanel;
