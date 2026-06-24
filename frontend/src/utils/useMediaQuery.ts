import { useEffect, useState } from 'react';

// Subscribe to a CSS media query and re-render when it changes. Guards against
// environments without matchMedia (jsdom in tests, SSR) by returning false, so
// the mobile-only chrome stays out of the DOM during unit tests.
export function useMediaQuery(query: string): boolean {
  const getMatch = () => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
      return false;
    }
    return window.matchMedia(query).matches;
  };

  const [matches, setMatches] = useState(getMatch);

  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
      return;
    }

    const mql = window.matchMedia(query);
    const onChange = () => setMatches(mql.matches);

    onChange();

    // Safari/iOS < 14 only expose the deprecated addListener/removeListener.
    // Fall back to those so the effect never throws and takes down the app.
    if (typeof mql.addEventListener === 'function') {
      mql.addEventListener('change', onChange);
      return () => mql.removeEventListener('change', onChange);
    }

    mql.addListener(onChange);
    return () => mql.removeListener(onChange);
  }, [query]);

  return matches;
}
