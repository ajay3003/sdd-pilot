# BirkNext Architecture Overview

## High-Level Architecture

BirkNext consists of:

- Blazor WebAssembly frontend
- ASP.NET Core backend
- GraphQL API
- PostgreSQL persistence
- Deterministic extraction engine

## Extraction Pipeline

```text
Paste Text
→ Normalize
→ Filter
→ Extract
→ Classify
→ Review
→ Save Selected Items
```

## Core Principles
- Deterministic extraction
- Review-before-save
- Client-side extraction ownership
- Safe observability
- Stable GraphQL contracts

## Evolution
US1:
- Manual scenario management

US2:
- Deterministic extraction workflow

US3:
- Internal deterministic rule engine

US4:
- Configurable deterministic extraction rules
