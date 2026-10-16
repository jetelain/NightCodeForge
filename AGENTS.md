
Unless requested, run tests without end-to-end tests with `dotnet test --filter "Category!=Acceptance&Category!=E2E"`

In service constructors, avoid using optional parameters. You should require all services you need in order to work properly. Avoid trying to have a fallback strategy if a service is not available. If a service is not available, it's better to let the application fail fast rather than silently using a default value that might not be appropriate for the use case. For tests use mocks (with Moq), fakes or stubs when needed.

Clean break policy : we are still in early developpement stage. Do not try to have backward compatibility. Do not hesitate to break anything if it helps us go faster. Do not hesitate to propose new architectural patterns or to refactor existing code if it helps us go faster. Do not hesitate to question existing decisions and to propose new ones. This is a fast moving project and we need to be agile.
