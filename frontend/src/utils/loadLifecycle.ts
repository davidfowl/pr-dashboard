type MutableRef<T> = {
  current: T;
};

export function beginAbortableLoad(
  versionRef: MutableRef<number>,
  abortControllerRef: MutableRef<AbortController | null>,
) {
  const loadVersion = versionRef.current + 1;
  versionRef.current = loadVersion;
  abortControllerRef.current?.abort();

  const abortController = new AbortController();
  abortControllerRef.current = abortController;

  const isCurrentLoad = () => versionRef.current === loadVersion;
  const finish = () => {
    if (isCurrentLoad()) {
      abortControllerRef.current = null;
    }
  };

  return {
    abortController,
    isCurrentLoad,
    finish,
  };
}

export function cancelAbortableLoad(
  versionRef: MutableRef<number>,
  abortControllerRef: MutableRef<AbortController | null>,
) {
  versionRef.current += 1;
  abortControllerRef.current?.abort();
  abortControllerRef.current = null;
}
