# VS-017 — Event Engine

External events are persisted as `WorkflowEvent`. A task in `WAITING_FOR_EVENT` whose `wait_ref` matches the event type transitions once to `READY`. Events and transitions are audited and survive restarts; tasks already completed are not re-executed.