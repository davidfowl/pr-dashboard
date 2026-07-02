# Product

## Register

product

## Users

Engineers and maintainers working in a focused operations context. They use the PR dashboard to prioritize review work and the deployed-instance canvas to inspect and manage the Azure-hosted app without switching to raw Azure CLI output.

## Product Purpose

Aspire Team App helps the team quickly understand pull request queues, urgent review work, ship-week status, and production app health. Success means the interface makes the next operational decision obvious: what needs attention, what is healthy, what failed, and what action is safe to take.

## Brand Personality

Polished, dense, trustworthy. The product should feel like an expert internal tool: calm under pressure, crisp in hierarchy, and explicit about operational state.

## Anti-references

Avoid an Azure portal clone: dense chrome, low hierarchy, and raw resource metadata competing with the user's task. Avoid generic SaaS dashboard tropes such as decorative metrics, marketing polish, and hero-stat layouts. Avoid feeling like a terminal wrapper that merely exposes logs and commands without product affordances.

## Design Principles

1. Prioritize the next operational decision over decorative completeness.
2. Use hierarchy to separate human-readable status from lower-level infrastructure details.
3. Keep controls familiar, explicit, and safe for production operations.
4. Preserve density where it helps engineers scan, but make primary state readable at a glance.
5. Prefer trustworthy restraint over novelty.

## Accessibility & Inclusion

Target WCAG AA contrast, visible keyboard focus, clear async/error/success states, and reduced-motion-safe transitions. The interface should work for dense scanning, keyboard-driven operation, and color-blind users by pairing color with labels or shape.
