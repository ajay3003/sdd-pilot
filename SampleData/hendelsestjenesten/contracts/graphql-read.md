# Contract — GraphQL Read API

**Consumer**: Saksbehandlere via presentation layer (Blazor WASM through YARP reverse proxy)
**Endpoint**: `/graphql`
**Auth**: OAuth2/OIDC Bearer token (EntraID) — validated via Autorisasjon API
**Full SDL**: `docs/Hendelsestjenesten-GraphQLskjema-(SDL).md`

---

## Queries

### hentHendelserForBarn
Requires operation: `Hendelse:HentHendelserForBarn`

```graphql
hentHendelserForBarn(
  barnId:             ID!
  hendelsesTypeKoder: [String!]  # filter; null = all types
  side:               Int        # page number, starts at 1
  antallPerSide:      Int        # default 25, max 100
): PaginertHendelseResultat!
```

Returns `HendelseSammendrag` list: id, type code/name, fraDato, sted, antallVersjoner.
Publishes leselogg to `revisjon.leselogg` after successful fetch (GL-32).

### hentHendelse
Requires operation: `Hendelse:HentHendelse`

```graphql
hentHendelse(hendelsesId: ID!): HendelseDetalj
```

Returns full detail with `aktivVersjon` and `versjonsHistorikk`.
Field-level access control (null if missing permission):
- `involverte` — requires `Hendelse:SeInvolverte`
- `inngrepDetalj` — requires `Hendelse:SeInngrepDetalj`
- `rommingsDetalj` — requires `Hendelse:SeRommingsDetalj`

Publishes leselogg after successful fetch (GL-32).

### Reference Queries
- `hentHendelsesTyper` — active types (valid login only)
- `hentHjemmelTyper(kunGjeldende: Boolean)` — hjemmel types (valid login only)
- `helse` — health status (no auth required)

---

## Access Control Rule

Failing authorization → field is absent in response (not an error). This is by design (FR-05).
Server returns HTTP 503 if Autorisasjon API is unreachable (fail-closed, GL-25).

---

## Operations Registered at Startup (PS-06)

```
Hendelse:HentHendelserForBarn
Hendelse:HentHendelse
Hendelse:SeInvolverte
Hendelse:SeInngrepDetalj
Hendelse:SeRommingsDetalj
```

Published to Service Bus at startup; service refuses to start on registration failure (GL-09).
