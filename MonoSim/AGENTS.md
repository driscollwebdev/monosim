# Coding conventions

Apply these conventions to all existing and new code in this project, including
tests and examples:

- Always wrap control-flow bodies in curly braces, even when the body contains
  only one statement. This includes `if`, `else`, loops, and similar constructs.
- Do not use nested ternary expressions or multiple ternary operators in a
  single assignment expression. Split the decisions into separate assignments
  or explicit control-flow blocks instead. A single ternary is allowed.

Preserve behavior when applying these style changes. Run the build and existing
strategy checks after modifying C# code.
