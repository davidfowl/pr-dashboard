import { useCallback, useEffect, useId, useRef, useState } from 'react';
import type { CSSProperties } from 'react';

type HelpTooltipProps = {
  label: string;
};

type TooltipPlacement = 'bottom' | 'top';

type TooltipSize = {
  width: number;
  height: number;
};

type TooltipState = {
  placement: TooltipPlacement;
  offsetX: number;
};

type TooltipStyle = CSSProperties & {
  '--tooltip-offset-x': string;
};

const tooltipOffsetRem = 0.55;
const tooltipViewportPaddingMultiplier = 1.5;
const fallbackRootFontSize = 16;

function getRootFontSize() {
  const rootFontSize = Number.parseFloat(
    window.getComputedStyle(document.documentElement).fontSize,
  );

  return Number.isFinite(rootFontSize) ? rootFontSize : fallbackRootFontSize;
}

function getViewportPadding(anchor: HTMLElement) {
  const appShell = anchor.closest<HTMLElement>('.app-shell');

  if (appShell) {
    const appShellPadding = Number.parseFloat(window.getComputedStyle(appShell).paddingLeft);

    if (Number.isFinite(appShellPadding)) {
      return appShellPadding * tooltipViewportPaddingMultiplier;
    }
  }

  return getRootFontSize() * tooltipViewportPaddingMultiplier;
}

function clamp(value: number, min: number, max: number) {
  return Math.min(Math.max(value, min), max);
}

function getTooltipState(
  triggerRect: DOMRect,
  tooltipSize: TooltipSize,
  viewportPadding: number,
): TooltipState {
  const viewportWidth = window.innerWidth;
  const viewportHeight = window.innerHeight;
  const tooltipOffset = tooltipOffsetRem * getRootFontSize();
  const spaceBelow = viewportHeight - viewportPadding - triggerRect.bottom - tooltipOffset;
  const spaceAbove = triggerRect.top - viewportPadding - tooltipOffset;
  const placement = spaceBelow < tooltipSize.height && spaceAbove > spaceBelow ? 'top' : 'bottom';
  const centeredLeft = triggerRect.left + triggerRect.width / 2 - tooltipSize.width / 2;
  const maxLeft = Math.max(viewportPadding, viewportWidth - viewportPadding - tooltipSize.width);
  const clampedLeft = clamp(centeredLeft, viewportPadding, maxLeft);

  return {
    placement,
    offsetX: clampedLeft - centeredLeft,
  };
}

function HelpTooltip({ label }: HelpTooltipProps) {
  const tooltipId = useId();
  const buttonRef = useRef<HTMLButtonElement>(null);
  const tooltipRef = useRef<HTMLSpanElement>(null);
  const [isTooltipActive, setIsTooltipActive] = useState(false);
  const [tooltipState, setTooltipState] = useState<TooltipState>({
    placement: 'bottom',
    offsetX: 0,
  });

  const updateTooltipPlacement = useCallback(() => {
    const button = buttonRef.current;
    const tooltip = tooltipRef.current;

    if (!button || !tooltip) {
      return;
    }

    const triggerRect = button.getBoundingClientRect();
    const tooltipRect = tooltip.getBoundingClientRect();
    const viewportPadding = getViewportPadding(button);

    setTooltipState(getTooltipState(triggerRect, tooltipRect, viewportPadding));
  }, []);

  const activateTooltip = useCallback(() => {
    updateTooltipPlacement();
    setIsTooltipActive(true);
  }, [updateTooltipPlacement]);

  const deactivateTooltip = useCallback(() => {
    setIsTooltipActive(false);
  }, []);

  useEffect(() => {
    if (!isTooltipActive) {
      return undefined;
    }

    updateTooltipPlacement();
    window.addEventListener('resize', updateTooltipPlacement);
    window.addEventListener('scroll', updateTooltipPlacement, true);

    return () => {
      window.removeEventListener('resize', updateTooltipPlacement);
      window.removeEventListener('scroll', updateTooltipPlacement, true);
    };
  }, [isTooltipActive, label, updateTooltipPlacement]);

  const tooltipClassName = [
    'logic-help-tooltip',
    `placement-${tooltipState.placement}`,
    isTooltipActive ? 'visible' : undefined,
  ].filter(Boolean).join(' ');
  const tooltipStyle = {
    '--tooltip-offset-x': `${tooltipState.offsetX}px`,
  } as TooltipStyle;

  return (
    <button
      ref={buttonRef}
      type="button"
      className="logic-help"
      aria-label={label}
      aria-describedby={isTooltipActive ? tooltipId : undefined}
      onBlur={deactivateTooltip}
      onFocus={activateTooltip}
      onPointerEnter={activateTooltip}
      onPointerLeave={deactivateTooltip}
    >
      ?
      <span
        ref={tooltipRef}
        id={tooltipId}
        role="tooltip"
        className={tooltipClassName}
        style={tooltipStyle}
      >
        {label}
      </span>
    </button>
  );
}

export default HelpTooltip;
