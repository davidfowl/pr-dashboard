import type {
  ActivityModel,
  MergeableState,
  PullRequestSummary,
  TimelineItem,
  TimelineStoryEntry,
  TriageModel,
} from '../../types';
import ActivityPanel from './ActivityPanel';
import ChecksPanel from './ChecksPanel';
import DeveloperPanel from './DeveloperPanel';
import MilestonePanel from './MilestonePanel';
import RawActivityTimeline from './RawActivityTimeline';
import TriagePanel from './TriagePanel';

type DetailViewProps = {
  activeRepo: string;
  selectedTitle: string;
  selectedPullRequest: PullRequestSummary | null;
  timelineLoading: boolean;
  timelineItems: TimelineItem[];
  triageModel: TriageModel | null;
  activityModel: ActivityModel | null;
  groupedTimeline: Record<string, TimelineStoryEntry[]>;
  mergeableState: MergeableState | null;
  onBack: () => void;
};

function DetailView({
  activeRepo,
  selectedTitle,
  selectedPullRequest,
  timelineLoading,
  timelineItems,
  triageModel,
  activityModel,
  groupedTimeline,
  mergeableState,
  onBack,
}: DetailViewProps) {
  return (
    <section className="panel timeline-panel details-panel" aria-labelledby="timeline-heading">
      <div className="timeline-header">
        <div>
          <p className="eyebrow">{activeRepo}</p>
          <h2 id="timeline-heading">{selectedTitle}</h2>
        </div>
        <div className="details-actions">
          <button type="button" onClick={onBack}>
            Back to dashboard
          </button>
          {selectedPullRequest && (
            <a href={selectedPullRequest.htmlUrl} target="_blank" rel="noreferrer">
              Open on GitHub
            </a>
          )}
        </div>
      </div>

      {timelineLoading ? (
        <p className="empty-state">Loading timeline...</p>
      ) : timelineItems.length === 0 ? (
        <p className="empty-state">Select a pull request to load its timeline.</p>
      ) : (
        <div>
          {triageModel && <TriagePanel model={triageModel} />}

          {selectedPullRequest?.checks && (
            <ChecksPanel checks={selectedPullRequest.checks} mergeableState={mergeableState} />
          )}

          {activityModel && <ActivityPanel model={activityModel} />}

          {triageModel && triageModel.participants.length > 0 && (
            <DeveloperPanel participants={triageModel.participants} />
          )}

          {triageModel && <MilestonePanel milestones={triageModel.milestones} />}

          <RawActivityTimeline groupedTimeline={groupedTimeline} />
        </div>
      )}
    </section>
  );
}

export default DetailView;
