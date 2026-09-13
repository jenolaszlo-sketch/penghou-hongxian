# ADR-0002: Experience projections are provider-neutral and disposable

- Status: Accepted
- Date: 2026-09-13

## Context

Useful experience recall needs relationships, lexical search, and eventually
semantic retrieval. A local LatticeDB projection is a strong first fit, but
making its data model or query language Hongxian's public contract would bind
the product to one storage engine. Future consumers may need Neo4j, another
graph database, or a remote managed projection service.

A lowest-common-denominator storage abstraction would avoid vendor names while
still leaking storage concerns and limiting useful provider capabilities.

## Decision

Hongxian defines logical projection and recall contracts in terms of evidence,
experience, provenance, freshness, and bounded queries. Provider packages map
those contracts to storage-specific graph, lexical, vector, or remote query
capabilities.

The first planned experience provider is LatticeDB. It is an implementation and
operational choice, not canonical state and not Hongxian's permanent storage
identity. A Neo4j or remote provider must be addable without changing evidence
envelopes or Fuwen/Baize consumer contracts.

The boundary will use:

- immutable evidence references as the provenance anchor;
- projector identity, schema/policy version, verified source head, and durable
  checkpoint as the projection position;
- provider-neutral logical requests and results for common recall operations;
- explicit provider capability reporting for graph traversal, lexical search,
  vector search, hybrid ranking, as-of support, and remote consistency;
- deterministic fallback or a typed unsupported-capability result rather than
  silently changing query meaning;
- explicit host registration rather than dynamic provider discovery.

Provider-native administration and advanced queries may exist on provider
types, but must not leak into core evidence or portable recall contracts.

## Consequences

- Projection stores may be dropped and rebuilt from verified evidence.
- Multiple projections can coexist for comparison, migration, or specialized
  workloads.
- Projection results identify the provider, policy/schema revision, checkpoint,
  and evidence from which they were derived.
- Remote providers introduce authentication, availability, disclosure, and
  consistency concerns that remain host/provider responsibilities.
- Provider conformance tests must validate portable semantics without requiring
  every provider to implement every optional capability.
- The existing `Penghou.Hongxian.Sqlite` composition remains valid while the
  new boundary is developed; package splitting is deferred until it produces a
  clearer dependency or deployment boundary.

## Non-goals

- The core does not define a generic database, graph API, SQL dialect, or Cypher
  abstraction.
- The projection does not become authoritative because it is durable or remote.
- Provider capability differences will not be hidden behind misleading success
  results.
- Hongxian does not copy externally owned artifacts into its projection merely
  to make them searchable.

