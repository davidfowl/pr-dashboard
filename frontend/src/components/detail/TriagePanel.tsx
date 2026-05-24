import type { TriageModel } from '../../types';

type TriagePanelProps = {
  model: TriageModel;
};

function TriagePanel({ model }: TriagePanelProps) {
  return (
    <section className="triage-panel" aria-label="Pull request triage">
      <article className={`triage-action ${model.action.tone ?? 'muted'}`}>
        <span>Next action</span>
        <strong>{model.action.label}</strong>
        <p>{model.why}</p>
      </article>
      <article className="triage-card">
        <span>Waiting on</span>
        <strong>{model.waitingOn}</strong>
        <div className="triage-signals">
          {model.signals.map((signal) => (
            <span key={signal.label} className={`attention-signal ${signal.tone ?? 'muted'}`}>
              {signal.label}
            </span>
          ))}
        </div>
      </article>
    </section>
  );
}

export default TriagePanel;
