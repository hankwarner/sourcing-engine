# README
Azure HTTP functions to provide order sourcing information based on customer shipping information and item inventory.

# Open API Definitions

https://fergusonsourcingengine.azurewebsites.net/api/swagger/ui?code=yHPrDXb5SyiWqMaA74va2ZGr/cPQs/Bzj7SWuVKlvb1Gc8PWBjXIjg==


# SourceOrderFromSite
Endpoint for sourcing orders from Ferguson.com. Order sourcing information will appear on a dashboard for Ferguson associates, along with messaging containing specific sourcing details.


## Route
`order/source`


## Body
Accepts a JSON object with the following key-value pairs:


### Required
 **string** `atgOrderId`

 **string** `sellWhse`: Branch ID of the selling warehouse. 

**object** `shipping`
      
   **object** `shipTo`

   **string** `address1`

   **string** `address2` 
   
   **string** `city`
   
   **string** `state` 
   
   **string** `zip` 




## Example Request
```
{
  "taxExempt": "N",
  "atgOrderId": "W639300633",
  "custAccountId": "POLLARDWTR",
  "customerId": "69986",
  "taxAmount": "1.36",
  "customerName": "Thomas Anderson",
  "userEmail": "mr_anderson@neo.com",
  "orderSubmitDate": "2020-07-09 08:16:58",
  "orderEntryDate": "2020-07-09 08:16:58",
  "orderRequiredDate": "2020-07-09",
  "shipFromWhse": "3326",
  "sellWhse": "3326",
  "notes": "Standard",
  "paymentOnAccount": {
    "payment": {
      "cardType": "VI",
      "address1": "1995 Poplar Ridge Road",
      "address2": "",
      "cardholderName": "Thomas Anderson",
      "cardExpirationDate": "2022-08",
      "city": "Aurora",
      "state": "NY",
      "zip": "13026",
      "token": "-E803-2508-3PZ9E1E2YTAF7E",
      "cardMaskedNumber": "XXXXXXXXXXXX2508"
    }
  },
  "shipping": {
    "tax": "0.0",
    "shipViaCode": "OT",
    "price": "0.0",
    "shipTo": {
      "name": "Thomas Anderson",
      "address1": "1995 Poplar Ridge Rd",
      "address2": "",
      "city": "Aurora",
      "country": "US",
      "state": "NY",
      "id": "M",
      "shipInstructionsAttention": "Thomas Anderson",
      "zip": "130269718",
      "shipInstructionsPhoneNumberAreaDialing": "315",
      "shipInstructionsPhoneNumberDialNumber": "7295356"
    }
  },
  "items": [
    {
      "priceFormula": "3-0.000",
      "priceColumn": "135",
      "extendedPrice": "17.0",
      "unitPriceCode": "EA",
      "leadLawFlag": "Y",
      "promotionMultiplier": "1",
      "distributedTax": "1.36",
      "description": "ci8318000259",
      "unitPrice": "17.0",
      "netPrice": "17.0",
      "quantity": "1",
      "masterProdId": "5123328"
    }
  ],
  "sourceSystem": "POL",
  "taxCode": "NY0503"
}
```



## Return
ATG order object containing all information from the initial order, plus the branch ID of the suggested "ship from" location for each item.

```
{
  "shipFrom": "107a",
  "sourcingMessage": "Sourced by line.",
  "processSourcing": true,
  "taxExempt": "N",
  "atgOrderId": "W639300633",
  "custAccountId": "POLLARDWTR",
  "customerId": "69986",
  "taxAmount": "1.36",
  "customerName": "Thomas Anderson",
  "userEmail": "mr_anderson@neo.com",
  "orderSubmitDate": "2020-07-09 08:16:58",
  "orderEntryDate": "2020-07-09 08:16:58",
  "orderRequiredDate": "2020-07-09",
  "shipFromWhse": "3326",
  "sellWhse": "3326",
  "notes": "Standard",
  "sourcingMessage": "",
  "paymentOnAccount": {
    "payment": {
      "cardType": "VI",
      "address1": "1995 Poplar Ridge Road",
      "address2": "",
      "cardholderName": "Thomas Anderson",
      "cardExpirationDate": "2022-08",
      "city": "Aurora",
      "state": "NY",
      "zip": "13026",
      "token": "-E803-2508-3PZ9E1E2YTAF7E",
      "cardMaskedNumber": "XXXXXXXXXXXX2508"
    }
  },
  "shipping": {
    "tax": "0.0",
    "shipViaCode": "OT",
    "price": "0.0",
    "shipTo": {
      "name": "Thomas Anderson",
      "address1": "1995 Poplar Ridge Rd",
      "address2": "",
      "city": "Aurora",
      "country": "US",
      "state": "NY",
      "id": "M",
      "shipInstructionsAttention": "Thomas Anderson",
      "zip": "130269718",
      "shipInstructionsPhoneNumberAreaDialing": "315",
      "shipInstructionsPhoneNumberDialNumber": "7295356"
    }
  },
  "items": [
    {
      "priceFormula": "3-0.000",
      "priceColumn": "135",
      "extendedPrice": "17.0",
      "unitPriceCode": "EA",
      "leadLawFlag": "Y",
      "promotionMultiplier": "1",
      "distributedTax": "1.36",
      "description": "ci8318000259",
      "unitPrice": "17.0",
      "netPrice": "17.0",
      "quantity": "1",
      "masterProdId": "5123328",
      "shipFrom": "533"
    }
  ],
  "sourceSystem": "POL",
  "taxCode": "NY0503"
}
```
