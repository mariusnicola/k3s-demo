# Build context: app/web
FROM node:22-slim AS build
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci
COPY index.html vite.config.* tsconfig*.json ./
COPY src/ src/
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.29-alpine
COPY --from=build /src/dist /usr/share/nginx/html
RUN printf '%s\n' 'server {' '  listen 8080;' '  root /usr/share/nginx/html;' '  location / { try_files $uri /index.html; }' '}' > /etc/nginx/conf.d/default.conf
