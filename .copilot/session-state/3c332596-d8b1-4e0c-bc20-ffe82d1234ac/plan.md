# Plan: Fix compound property serialization (Issue #20)

## Problem
`FormatPropertyValue` in `DevFlowAgentService.cs` loses detail for compound MAUI types:
- **Gradient brushes**: Only show stop count, not colors/offsets — clients can't reconstruct gradients
- **LayoutOptions**: `ToString()` doesn't distinguish `Expands` flag  
- **ItemsLayout**: `ToString()` loses orientation/spacing details

## Approach
Enhance `FormatPropertyValue` switch cases to serialize compound types with full detail. Keep the human-readable string format consistent with existing patterns.

## Todos

- [ ] fix-gradient-brushes — Enhance LinearGradientBrush and RadialGradientBrush to include gradient stop colors and offsets
- [ ] add-layout-options — Add LayoutOptions formatting with Alignment + Expands  
- [ ] add-items-layout — Add LinearItemsLayout and GridItemsLayout formatting
- [ ] build-and-test — Build, run tests, verify with live app
- [ ] create-pr — Create branch and PR for issue #20
