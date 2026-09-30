# RabbitMQ with the Prometheus plugin enabled, for Azure Container Apps.
#
# docker-compose does this by bind-mounting deploy/rabbitmq/enabled_plugins.
# Container Apps has no bind mounts, so the plugin is enabled at build time.
# Build context: repository root.
FROM rabbitmq:3-management

RUN rabbitmq-plugins enable --offline rabbitmq_prometheus
