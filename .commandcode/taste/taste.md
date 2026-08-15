# Coding Style & Architecture
See [coding-style-&-architecture/taste.md](coding-style-&-architecture/taste.md)
# Tooling & Process
See [tooling-&-process/taste.md](tooling-&-process/taste.md)
# Product & Delivery

- Prefers self-contained installs that bundle dependencies over requiring users to install dev tooling. Confidence: 0.85
- Privacy-first: no telemetry of user content; network and startup features are opt-in with consequences disclosed. Confidence: 0.8
- Designs for data recovery; never silently loses user work. Confidence: 0.85
- Cancel semantics preserve user work (cancel ≠ delete): canceling a recording stops capture and finalizes/preserves the transcript, marking the session canceled rather than discarding content. Confidence: 0.7
- Prefers polished, minimal consumer-grade UI over a developer-tool aesthetic. Confidence: 0.7

# Communication

- Prefers structured, evidence-based reports covering verified versions, authoritative sources, what works, discovered problems, and recommendations. Confidence: 0.75
