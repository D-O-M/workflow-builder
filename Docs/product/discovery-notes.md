# User Profile Discovery Notes

**Source:** project discovery interview, 4-5 November 2026. **Status:** context for future documentation and product work.

These notes preserve specific design ideas and open questions raised during discovery. The motivations and needs behind them are described in the [stakeholder roles](../stakeholder-roles/README.md).

## Credential management

Discovery identified a need for a separate management-plane UI for setting credentials. It would interface with Azure Key Vault or AWS Secrets Manager according to the cloud chosen. The platform/cloud team manages the secure credential store and makes the required credentials available to the system.

This is identified follow-up work. The interaction design, interfaces, and implementation remain to be defined separately.

## Plugin development guidance

The interview included expectations for unit tests covering each Plugin, release notes explaining breaking changes and their caveats, and versioning that prevents a breaking change from being adopted automatically by existing Blueprints.

The stated versioning intent was for a breaking change to increment at least the minor version, requiring an author to upgrade explicitly. Detailed automatic-adoption rules for compatible updates were not settled. These points should be developed in Plugin guidance, rather than treated as profile attributes or an assumed semantic-versioning standard.

## Delivery outcomes

Faster capability delivery, easier experimentation, reduced deployment effort, and greater service continuity are intended benefits of the design. Measurements and targets were not established during the profile interview.

## Core engine purpose

The 5 November clarification gives core engine developers responsibility for protecting the engine from business-domain-specific behaviour. Its intended purpose is generic orchestration: invoke Plugins, apply generic transformations to outputs, and supply the resulting inputs to subsequent work. It also persists runtime state and communicates what is happening within the engine and the work it manages.

Generic condition evaluation was raised as a possible future extension. This remains a tentative direction, with details to be explored separately.
