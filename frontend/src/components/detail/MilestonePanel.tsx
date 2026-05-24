import type { SignalMilestone } from '../../types';
import { formatRelative } from '../../utils/format';

type MilestonePanelProps = {
  milestones: SignalMilestone[];
};

function MilestonePanel({ milestones }: MilestonePanelProps) {
  return (
    <section className="milestone-panel" aria-label="Pull request signal milestones">
      <div className="section-title-row">
        <p className="eyebrow">Signals</p>
        <h3>What matters</h3>
      </div>
      <div className="milestone-list">
        {milestones.map((milestone) => (
          <article key={milestone.id} className={`milestone-card ${milestone.tone}`}>
            <div>
              <span className="event-pill">{milestone.event}</span>
              <time dateTime={milestone.occurredAt}>{formatRelative(milestone.occurredAt)}</time>
            </div>
            <strong>{milestone.title}</strong>
            {milestone.detail && <p>{milestone.detail}</p>}
            {milestone.url && (
              <a href={milestone.url} target="_blank" rel="noreferrer">
                View on GitHub
              </a>
            )}
          </article>
        ))}
      </div>
    </section>
  );
}

export default MilestonePanel;
