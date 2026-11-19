# Route a Process with Conditional Branch

In Blueprint Builder, search the Task Library for **Conditional Branch**. Place it after a task or Collection Iterator and connect one output channel to its input. Open the node settings in the right-hand drawer. The field picker uses the types and enum values declared by that channel.

For example, to select invoices that need approval, choose the **InvoiceTotal** field, **Greater than**, and a field operand of **ApprovalLimit**. Connect **True** to the approval Step and **False** to the ordinary processing Step. Each output carries the same input fields. Either output can end its route without a connection.

For a grouped rule such as `(Country starts with "GB" OR Tier equals Gold) AND NOT (Amount < 100)`, set the outer group to **All**, add an **Any** group for Country and Tier, then add the Amount comparison and select **NOT** on that comparison. Check the parentheses in the summary. Text comparisons are case-sensitive by default. Enable **Ignore case** on an individual comparison when needed.

If a field is optional, put **Has a value** before comparisons that need it in an **All** group. Use **Move up** and **Move down** to put the guard first within its group. The Engine evaluates conditions from top to bottom and stops as soon as the group result is known. An evaluated comparison that needs an absent or incorrectly typed value fails the Step and reports the field.

Enum comparisons use the member names advertised by the Plugin. If the Step reports a numeric enum value, check that the upstream Plugin serializes that output as a member name. The catalog provides names only, so the Engine cannot safely infer a name from a number.

Connect both routes to an ordinary shared Step to rejoin them. Map its required inputs on each route. The Engine waits for all active prerequisites and executes the shared Step once per Process or Collection Iterator item.

Use **Start Process** with inputs on either side of a boundary. In **Process Explorer**, open the condition to inspect the rule for that Blueprint version and the selected output. Steps exclusive to the other route show **Path not taken**. For iterator items, the drawer lists each outcome separately.
