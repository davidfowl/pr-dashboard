type HelpTooltipProps = {
  label: string;
};

function HelpTooltip({ label }: HelpTooltipProps) {
  return (
    <button
      type="button"
      className="logic-help"
      aria-label={label}
      data-tooltip={label}
    >
      ?
    </button>
  );
}

export default HelpTooltip;
