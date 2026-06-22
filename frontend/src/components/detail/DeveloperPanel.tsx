import type { CSSProperties } from 'react';
import type { TriageParticipant } from '../../types';
import { colorForText } from '../../utils/format';
import GitHubAvatar from '../GitHubAvatar';

type DeveloperPanelProps = {
  participants: TriageParticipant[];
};

function DeveloperPanel({ participants }: DeveloperPanelProps) {
  return (
    <section className="developer-panel" aria-label="Per-developer timeline stats">
      <div className="section-title-row">
        <p className="eyebrow">People</p>
        <h3>Who is involved</h3>
      </div>
      <div className="developer-grid">
        {participants.map((participant) => (
          <article
            key={participant.actor}
            className="developer-card"
            style={{ '--developer-accent': colorForText(participant.actor) } as CSSProperties}
          >
            <div className="developer-card-header">
              <GitHubAvatar login={participant.actor} />
              <span>
                <strong>{participant.actor}</strong>
                <em>{participant.role}</em>
              </span>
            </div>
            <p>{participant.summary}</p>
          </article>
        ))}
      </div>
    </section>
  );
}

export default DeveloperPanel;
