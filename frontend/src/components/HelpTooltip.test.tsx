// @vitest-environment jsdom

import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { afterEach, describe, expect, it } from 'vitest';
import HelpTooltip from './HelpTooltip';

type ActEnvironment = typeof globalThis & {
  IS_REACT_ACT_ENVIRONMENT?: boolean;
};

(globalThis as ActEnvironment).IS_REACT_ACT_ENVIRONMENT = true;

describe('HelpTooltip', () => {
  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('uses the centered bottom placement by default', async () => {
    const { button, root, tooltip } = await renderTooltip();
    setViewportSize(320, 240);
    mockRect(button, rect(140, 100, 16, 16));
    mockRect(tooltip, rect(0, 0, 120, 40));

    await focusTooltip(button);

    expect(tooltip.className).toContain('placement-bottom');
    expect(tooltip.className).toContain('visible');
    expect(tooltip.style.getPropertyValue('--tooltip-offset-x')).toBe('0px');

    await unmountTooltip(root);
  });

  it('offsets the bottom placement when centered placement would overflow right', async () => {
    const { button, root, tooltip } = await renderTooltip();
    setViewportSize(320, 240);
    mockRect(button, rect(290, 100, 16, 16));
    mockRect(tooltip, rect(0, 0, 160, 40));

    await focusTooltip(button);

    expect(tooltip.className).toContain('placement-bottom');
    expect(tooltip.style.getPropertyValue('--tooltip-offset-x')).toBe('-88px');

    await unmountTooltip(root);
  });

  it('flips above the trigger when there is not enough room below', async () => {
    const { button, root, tooltip } = await renderTooltip();
    setViewportSize(320, 240);
    mockRect(button, rect(140, 210, 16, 16));
    mockRect(tooltip, rect(0, 0, 120, 40));

    await focusTooltip(button);

    expect(tooltip.className).toContain('placement-top');

    await unmountTooltip(root);
  });

  it('stays open until both focus and pointer hover are inactive', async () => {
    const { button, root, tooltip } = await renderTooltip();
    setViewportSize(320, 240);
    mockRect(button, rect(140, 100, 16, 16));
    mockRect(tooltip, rect(0, 0, 120, 40));

    await focusTooltip(button);
    await pointerEnterTooltip(button);
    await pointerLeaveTooltip(button);

    expect(tooltip.className).toContain('visible');
    expect(button.getAttribute('aria-describedby')).toBe(tooltip.id);

    await pointerEnterTooltip(button);
    await blurTooltip(button);

    expect(tooltip.className).toContain('visible');
    expect(button.getAttribute('aria-describedby')).toBe(tooltip.id);

    await pointerLeaveTooltip(button);

    expect(tooltip.className).not.toContain('visible');
    expect(button.getAttribute('aria-describedby')).toBeNull();

    await unmountTooltip(root);
  });
});

async function renderTooltip() {
  const host = document.createElement('div');
  host.className = 'app-shell';
  host.style.padding = '20px';
  document.body.append(host);
  const root = createRoot(host);

  await act(async () => {
    root.render(<HelpTooltip label="Explains how this section is calculated." />);
  });

  const button = host.querySelector('button');
  const tooltip = host.querySelector<HTMLElement>('[role="tooltip"]');

  if (!button || !tooltip) {
    throw new Error('Tooltip did not render');
  }

  return { button, host, root, tooltip };
}

async function focusTooltip(button: HTMLButtonElement) {
  await act(async () => {
    button.focus();
  });
}

async function blurTooltip(button: HTMLButtonElement) {
  await act(async () => {
    button.blur();
  });
}

async function pointerEnterTooltip(button: HTMLButtonElement) {
  await dispatchPointerEvent(button, 'pointerover');
}

async function pointerLeaveTooltip(button: HTMLButtonElement) {
  await dispatchPointerEvent(button, 'pointerout');
}

async function dispatchPointerEvent(button: HTMLButtonElement, eventName: string) {
  await act(async () => {
    button.dispatchEvent(new Event(eventName, { bubbles: true }));
  });
}

async function unmountTooltip(root: ReturnType<typeof createRoot>) {
  await act(async () => {
    root.unmount();
  });
}

function mockRect(element: Element, nextRect: DOMRect) {
  Object.defineProperty(element, 'getBoundingClientRect', {
    configurable: true,
    value: () => nextRect,
  });
}

function rect(left: number, top: number, width: number, height: number) {
  return {
    bottom: top + height,
    height,
    left,
    right: left + width,
    top,
    width,
    x: left,
    y: top,
    toJSON: () => ({}),
  } as DOMRect;
}

function setViewportSize(width: number, height: number) {
  Object.defineProperty(window, 'innerWidth', {
    configurable: true,
    value: width,
  });
  Object.defineProperty(window, 'innerHeight', {
    configurable: true,
    value: height,
  });
}
