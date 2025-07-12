# Use the latest Node 24 (Current) version
FROM node:24-alpine

WORKDIR /app

# Install Angular CLI v20 and dependencies
COPY package*.json .

RUN npm install
RUN npm install -g @angular/cli@20

# Copy app code
COPY . .

# Expose dev port
EXPOSE 4200

# Use polling for Docker file changes
CMD ["ng", "serve", "--host", "0.0.0.0", "--poll=2000"]
