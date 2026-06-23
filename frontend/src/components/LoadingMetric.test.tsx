// @vitest-environment jsdom

import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { afterEach, describe, expect, it } from 'vitest';
import LoadingMetric from './LoadingMetric';

type ActEnvironment = typeof globalThis & {
  IS_REACT_ACT_ENVIRONMENT?: boolean;
};

(globalThis as ActEnvironment).IS_REACT_ACT_ENVIRONMENT = true;

describe('LoadingMetric', () => {
  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('shows an indeterminate placeholder before the first completed load', async () => {
    const { host, root } = await renderMetric(7, true, false);

    expect(host.textContent).toBe('—');
    expect(host.querySelector('strong')?.getAttribute('aria-label')).toBe('Count is loading');

    await unmountMetric(root);
  });

  it('pins the previous value during refresh and shows the final value when loading completes', async () => {
    const { host, root, rerender } = await renderMetric(3, false, true);

    expect(host.textContent).toBe('3 PRs');

    await rerender(7, true, true);
    expect(host.textContent).toBe('3 PRs');

    await rerender(9, true, true);
    expect(host.textContent).toBe('3 PRs');

    await rerender(9, false, true);
    expect(host.textContent).toBe('9 PRs');

    await unmountMetric(root);
  });
});

async function renderMetric(value: number, loading: boolean, hasLoaded: boolean) {
  const host = document.createElement('div');
  document.body.append(host);
  const root = createRoot(host);

  const render = async (nextValue: number, nextLoading: boolean, nextHasLoaded: boolean) => {
    await act(async () => {
      root.render(
        <LoadingMetric
          value={nextValue}
          loading={nextLoading}
          hasLoaded={nextHasLoaded}
          formatValue={(count) => `${count} PRs`}
        />,
      );
    });
  };

  await render(value, loading, hasLoaded);
  return { host, root, rerender: render };
}

async function unmountMetric(root: ReturnType<typeof createRoot>) {
  await act(async () => {
    root.unmount();
  });
}
