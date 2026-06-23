import type { ReactNode } from 'react';
import LoadingBadge from '../LoadingBadge';
import LoadingMetric from '../LoadingMetric';

export type DrilldownTileTone = 'success' | 'warning' | 'danger' | 'accent';

export type DrilldownTile<TId extends string = string> = {
  id: TId;
  label: string;
  count: number;
  summary: string;
  tone?: DrilldownTileTone;
  loading?: boolean;
  hasLoaded?: boolean;
  placeholder?: boolean;
};

type TileDrilldownProps<TId extends string, TTile extends DrilldownTile<TId>> = {
  ariaLabel: string;
  idPrefix: string;
  selectedId: TId;
  tileListLabel: string;
  tiles: readonly TTile[];
  onSelect: (id: TId) => void;
  renderDetails: (tile: TTile) => ReactNode;
  className?: string;
};

function TileDrilldown<TId extends string, TTile extends DrilldownTile<TId>>({
  ariaLabel,
  className,
  idPrefix,
  selectedId,
  tileListLabel,
  tiles,
  onSelect,
  renderDetails,
}: TileDrilldownProps<TId, TTile>) {
  const selectedTile = tiles.find((tile) => tile.id === selectedId) ?? tiles[0];

  if (!selectedTile) {
    return null;
  }

  const activeId = selectedTile.id;

  return (
    <section className={['tile-drilldown', className].filter(Boolean).join(' ')} aria-label={ariaLabel}>
      <div className="drilldown-tiles" role="tablist" aria-label={tileListLabel}>
        {tiles.map((tile) => (
          <button
            key={tile.id}
            type="button"
            id={tabId(idPrefix, tile.id)}
            className={[
              activeId === tile.id ? 'selected' : undefined,
              tile.tone,
              tile.placeholder ? 'placeholder' : undefined,
            ].filter(Boolean).join(' ')}
            onClick={() => onSelect(tile.id)}
            disabled={tile.placeholder}
            role="tab"
            aria-disabled={tile.placeholder || undefined}
            aria-controls={panelId(idPrefix, tile.id)}
            aria-selected={activeId === tile.id}
          >
            <div className="drilldown-tile-title">
              <span className="drilldown-tile-label">{tile.label}</span>
              {tile.loading && <LoadingBadge label={tile.hasLoaded ? 'Refreshing' : 'Loading'} />}
            </div>
            <LoadingMetric
              value={tile.count}
              loading={tile.loading === true}
              hasLoaded={tile.hasLoaded === true}
              formatValue={(count) => count.toLocaleString()}
              pendingLabel={`${tile.label} count is loading`}
            />
            <em>{tile.summary}</em>
          </button>
        ))}
      </div>

      <div
        id={panelId(idPrefix, activeId)}
        className="drilldown-content"
        role="tabpanel"
        aria-labelledby={tabId(idPrefix, activeId)}
      >
        {renderDetails(selectedTile)}
      </div>
    </section>
  );
}

function tabId(prefix: string, id: string) {
  return `${prefix}-${safeId(id)}-tab`;
}

function panelId(prefix: string, id: string) {
  return `${prefix}-${safeId(id)}-panel`;
}

function safeId(value: string) {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'item';
}

export default TileDrilldown;
