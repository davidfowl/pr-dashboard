export async function readJson<T>(response: Response): Promise<T> {
  if (response.ok) {
    return (await response.json()) as T;
  }

  const payload = await response.json().catch(() => null);
  const detail =
    typeof payload?.detail === 'string'
      ? payload.detail
      : typeof payload?.title === 'string'
        ? payload.title
        : `HTTP ${response.status}`;
  throw new Error(detail);
}

export async function readJsonLines<T>(response: Response, onItem: (item: T) => void): Promise<void> {
  if (!response.ok) {
    await readJson<never>(response);
    return;
  }

  if (!response.body) {
    throw new Error('Streaming responses are not supported by this browser.');
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';

  function readBufferedLines(flush = false) {
    const lines = buffer.split('\n');
    const remainingLine = lines.pop() ?? '';
    buffer = flush ? '' : remainingLine;

    for (const line of lines) {
      const trimmedLine = line.trim();
      if (trimmedLine) {
        onItem(JSON.parse(trimmedLine) as T);
      }
    }

    if (flush) {
      const trimmedLine = remainingLine.trim();
      if (trimmedLine) {
        onItem(JSON.parse(trimmedLine) as T);
      }
    }
  }

  while (true) {
    const { value, done } = await reader.read();
    if (done) {
      buffer += decoder.decode();
      readBufferedLines(true);
      return;
    }

    buffer += decoder.decode(value, { stream: true });
    readBufferedLines();
  }
}
