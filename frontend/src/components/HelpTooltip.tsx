type HelpTooltipProps = {
  label: string;
};

function HelpTooltip({ label }: HelpTooltipProps) {
  return (
    <span
      className="logic-help"
      tabIndex={0}
      role="note"
      aria-label={label}
      title={label}
      data-tooltip={label}
    >
      ?
    </span>
  );
}

export default HelpTooltip;
