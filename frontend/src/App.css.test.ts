import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

const css = readFileSync(new URL('./App.css', import.meta.url), 'utf8');

describe('pull request row layout CSS', () => {
  it('uses the same content-independent grid columns for every PR row variant', () => {
    const baseTemplate = gridTemplateColumnsFor('.attention-pr-row');
    const compactTemplate = gridTemplateColumnsFor('.attention-pr-row.compact-pr-action-marker-layout');

    expect(baseTemplate).toBe('var(--attention-pr-grid-columns)');
    expect(compactTemplate).toBe(baseTemplate);
    expect(compactTemplate).not.toMatch(/\b(fit-content|max-content|min-content|auto)\b/);
  });

  it('vertically centers action markers with the row text', () => {
    const markerBody = ruleBody('.attention-pr-action-marker');

    expect(cssDeclaration(markerBody, 'align-self')).toBe('center');
  });
});

function gridTemplateColumnsFor(selector: string) {
  const body = ruleBody(selector);
  const declaration = body.match(/grid-template-columns:\s*([^;]+);/);
  expect(declaration, `${selector} should set grid-template-columns`).not.toBeNull();
  return declaration?.[1].trim();
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
