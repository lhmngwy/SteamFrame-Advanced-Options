# Patch maintenance tests

## Work Guidance

- Use synthetic driver files and mocked delivery; the ordinary suite must not install the service or contact Pushover.
- Use fake clocks for polling and retry behavior. Assert save callback counts for idle-write regressions.
