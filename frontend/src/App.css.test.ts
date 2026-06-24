import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

const css = readFileSync(new URL('./App.css', import.meta.url), 'utf8');

describe('pull request row layout CSS', () => {
  it('uses content-independent grid columns and collapses the marker column on unmarked rows', () => {
    const unmarkedColumns = cssVariableFor('.attention-list', '--attention-pr-grid-columns');
    const markedColumns = cssVariableFor('.attention-list', '--attention-pr-marked-grid-columns');
    const baseTemplate = gridTemplateColumnsFor('.attention-pr-row');
    const compactTemplate = gridTemplateColumnsFor('.attention-pr-row.compact-pr-action-marker-layout');

    expect(baseTemplate).toBe('var(--attention-pr-grid-columns)');
    expect(compactTemplate).toBe('var(--attention-pr-marked-grid-columns)');
    expect(unmarkedColumns).toMatch(/\b0\s+minmax\(0,\s*1fr\)/);
    expect(markedColumns).toMatch(/\b7\.1rem\s+minmax\(0,\s*1fr\)/);
    expect(unmarkedColumns).not.toBe(markedColumns);
    expect(unmarkedColumns).not.toMatch(/\b(fit-content|max-content|min-content|auto)\b/);
    expect(markedColumns).not.toMatch(/\b(fit-content|max-content|min-content|auto)\b/);
    expect(compactTemplate).not.toMatch(/\b(fit-content|max-content|min-content|auto)\b/);
  });

  it('vertically centers action markers with the row text', () => {
    const markerBody = ruleBody('.attention-pr-action-marker');

    expect(cssDeclaration(markerBody, 'align-self')).toBe('center');
  });
});

function gridTemplateColumnsFor(selector: string) {
  const body = ruleBody(selector);
  return cssDeclaration(body, 'grid-template-columns');
}

function cssVariableFor(selector: string, property: string) {
  const body = ruleBody(selector);
  return cssDeclaration(body, property);
}

function ruleBody(selector: string) {
  const escapedSelector = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const rule = css.match(new RegExp(`${escapedSelector}\\s*\\{([^}]*)\\}`));
  expect(rule, `${selector} rule should exist`).not.toBeNull();
  return rule?.[1] ?? '';
}

function cssDeclaration(body: string, property: string) {
  const escapedProperty = property.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const declaration = body.match(new RegExp(`${escapedProperty}:\\s*([^;]+);`));
  expect(declaration, `${property} declaration should exist`).not.toBeNull();
  return declaration?.[1].trim();
}
