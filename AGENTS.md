
Unless requested, run tests without end-to-end tests with `dotnet test --filter "Category!=Acceptance&Category!=E2E"`

In service constructors, avoid using optional parameters. You should require all services you need in order to work properly. Avoid trying to have a fallback strategy if a service is not available. If a service is not available, it's better to let the application fail fast rather than silently using a default value that might not be appropriate for the use case. For tests use mocks (with Moq), fakes or stubs when needed.
