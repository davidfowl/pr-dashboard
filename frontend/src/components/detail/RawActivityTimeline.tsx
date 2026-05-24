import type { TimelineStoryEntry } from '../../types';
import { formatTime, truncate } from '../../utils/format';
import { storyHeadline } from '../../utils/models';

type RawActivityTimelineProps = {
  groupedTimeline: Record<string, TimelineStoryEntry[]>;
};

function RawActivityTimeline({ groupedTimeline }: RawActivityTimelineProps) {
  return (
    <details className="raw-activity">
      <summary>Raw activity</summary>
      <div className="timeline">
        {Object.entries(groupedTimeline).map(([day, items]) => (
          <section key={day} className="timeline-day" aria-label={day}>
            <h3>{day}</h3>
            {items.map((entry) => (
              <article key={entry.id} className={`timeline-item ${entry.kind}`}>
                <div className="timeline-dot" aria-hidden="true" />
                <div className="timeline-content">
                  <div className="timeline-row">
                    <span className="event-pill">{entry.event.replace(/_/g, ' ')}</span>
                    <time dateTime={entry.occurredAt}>{formatTime(entry.occurredAt)}</time>
                  </div>
                  <strong>{entry.kind === 'event' ? storyHeadline(entry.item) : entry.summary}</strong>
                  {entry.kind === 'event' && entry.item.body && <p>{truncate(entry.item.body, 420)}</p>}
                  {entry.kind === 'summary' && <p>{entry.detail}</p>}
                  {entry.kind === 'event' && entry.item.htmlUrl && (
                    <a href={entry.item.htmlUrl} target="_blank" rel="noreferrer">
                      View event
                    </a>
                  )}
                </div>
              </article>
            ))}
          </section>
        ))}
      </div>
    </details>
  );
}

export default RawActivityTimeline;
