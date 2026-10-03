FROM nginx:stable-alpine
COPY deploy/dev-gateway/hosted-intermediates.pem /tmp/hosted-intermediates.pem
RUN cat /tmp/hosted-intermediates.pem >> /etc/ssl/certs/ca-certificates.crt && rm /tmp/hosted-intermediates.pem
COPY deploy/dev-gateway/default.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 CMD wget -q -O /dev/null http://127.0.0.1/health || exit 1
