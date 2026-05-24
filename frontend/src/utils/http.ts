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
