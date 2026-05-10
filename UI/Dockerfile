# Use the latest Node 24 (Current) version
FROM node:24-alpine

WORKDIR /app

# Install project dependencies, including the local Angular CLI version.
COPY package*.json .

RUN npm install

# Copy app code
COPY . .

# Expose dev port
EXPOSE 4200

# Use polling for Docker file changes
CMD ["npm", "run", "start", "--", "--host", "0.0.0.0", "--poll=2000"]
