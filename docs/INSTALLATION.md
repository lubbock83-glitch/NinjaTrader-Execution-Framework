# Installation

These are NinjaScript source files. NinjaTrader compiles them in place, so
installation means pasting them into the NinjaScript editor rather than running
an installer.

## Indicators and strategies

1. In NinjaTrader 8: **Tools -> Edit NinjaScript -> Indicator** (or **Strategy**)
2. **New**, and give it the same name as the class in the file
3. Select all the generated code and delete it
4. Paste the file contents
5. **F5** to compile, and confirm `Compile succeeded` in the output window

`MasterTerminalBase` must be compiled **before** any strategy that inherits it,
otherwise the subclasses will not resolve.

## Bar types

Bar types follow the same paste-and-compile process under **BarsTypes**, with one
difference: **NinjaTrader must be restarted** before a newly compiled bar type
appears in the Data Series dropdown. It will not show up on a reload.

The four bar types in this repository register overlapping `BarsPeriodType` ids
and are intended to be installed **one at a time**. They are four answers to the
same question, not four components of one system.

## Dependencies

| Component | Requirement |
|---|---|
| `SignalBridge.cs` | Reference to **System.Web.Extensions** for `JavaScriptSerializer`. Right-click the NinjaScript project -> Add Reference, then recompile. |
| `CVDAbsorptionDivergence.cs` | The NinjaTrader **Order Flow** addon. Will not compile without it, deliberately — see the file header. |
| `nt8_bridge.py`, `execution_handler.py`, `renko_engine.py` | Python 3.10+. `renko_engine` additionally uses Numba for its JIT kernels. |

## Running the bridge

`SignalBridge` is an Indicator, so it is added to a chart like any other. Once
applied it opens a TCP listener bound to `127.0.0.1` and waits for the Python
side to connect. The listener is loopback-only by construction and is not
reachable from another machine.

Start NinjaTrader and apply the indicator first, then start the Python side. The
bridge prints its listening address to the NinjaScript output window, which is
the quickest way to confirm it came up.
