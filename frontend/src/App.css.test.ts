import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

const css = readFileSync(new URL('./App.css', import.meta.url), 'utf8');

describe('pull request row layout CSS', () => {
  it('uses the issue-row spacing model unless a PR row has an action marker', () => {
    const unmarkedColumns = cssVariableFor('.attention-list', '--attention-pr-grid-columns');
    const baseTemplate = gridTemplateColumnsFor('.attention-pr-row');
    const compactTemplate = gridTemplateColumnsFor('.attention-pr-row.compact-pr-action-marker-layout');
    const issueTemplate = gridTemplateColumnsFor('.attention-issue-row');
    const baseBody = ruleBody('.attention-pr-row');
    const sharedBody = ruleBody('.attention-pr-row,\n.attention-issue-row');

    expect(baseTemplate).toBe('var(--attention-pr-grid-columns)');
    expect(compactTemplate).toBe(baseTemplate);
    expect(unmarkedColumns).toMatch(/^5rem minmax\(10rem,\s*14rem\) minmax\(0,\s*1fr\)/);
    expect(issueTemplate).toMatch(/^5rem minmax\(10rem,\s*14rem\) minmax\(0,\s*1fr\)/);
    expect(cssDeclaration(baseBody, 'column-gap')).toBe(cssDeclaration(sharedBody, 'column-gap'));
    expect(unmarkedColumns).not.toMatch(/\b(fit-content|max-content|min-content|auto)\b/);
    expect(compactTemplate).not.toMatch(/\b(fit-content|max-content|min-content|auto)\b/);
  });

  it('does not define PR action marker styles', () => {
    expect(css).not.toContain('attention-pr-action-marker');
  });
});

function gridTemplateColumnsFor(selector: string) {
  const body = ruleBodyWithDeclaration(selector, 'grid-template-columns');
  return cssDeclaration(body, 'grid-template-columns');
}

function cssVariableFor(selector: string, property: string) {
  const body = ruleBodyWithDeclaration(selector, property);
  return cssDeclaration(body, property);
}

function ruleBodyWithDeclaration(selector: string, property: string) {
  const escapedSelector = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const rules = [...css.matchAll(new RegExp(`${escapedSelector}\\s*\\{([^}]*)\\}`, 'g'))];
  const body = rules.find((rule) => rule[1].includes(`${property}:`))?.[1];
  expect(body, `${selector} rule should set ${property}`).not.toBeUndefined();
  return body ?? '';
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
