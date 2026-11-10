# Wire changes (test fixture)

- Synthetic delivery (contract `0.9.0-utkast` → `1.0.0-draft`):
  - values `PRIVAT_INSTITUSJON` kept, `MOTTATT` → `RECEIVED`, `KUN_AVVIST` → `REJECTED_ONLY`, `MANGLER` → `MISSING`,
    `MOTTATT_IKKE_FORVENTET` → `RECEIVED_NOT_EXPECTED`, `AVVIST_IKKE_TILORDNET` → `REJECTED_UNATTRIBUTED`.
  - blob path `periode={p}/` → `period={p}/`.
