/** Merging cursor pages without duplicates: new items are appended, repeats by id are skipped. */
export function mergePages<T extends { id: string }>(prev: T[], next: T[]): T[] {
  const seen = new Set(prev.map((x) => x.id));
  return [...prev, ...next.filter((x) => !seen.has(x.id))];
}

/** Refreshing the first page (status polling): fresh items on top, previously loaded ones stay below. */
export function mergeFirstPage<T extends { id: string }>(prev: T[], first: T[]): T[] {
  const ids = new Set(first.map((x) => x.id));
  return [...first, ...prev.filter((x) => !ids.has(x.id))];
}
